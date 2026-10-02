using Microsoft.Data.SqlClient;

namespace SqlClone;

internal sealed record TableInfo(int ObjectId, string Schema, string Name)
{
    public string QualifiedName => Sql.Quote(Schema, Name);
}

internal sealed record ColumnInfo(string Name, bool IsAssemblyType, bool IsIdentity);

internal sealed record ModuleInfo(
    int ObjectId,
    string Schema,
    string Name,
    string Type,
    string? Definition,
    bool AnsiNulls,
    bool QuotedIdentifier,
    string? ParentSchema,
    string? ParentName,
    bool IsDisabled);

internal sealed record DatabaseTriggerInfo(string Name, string? Definition, bool AnsiNulls, bool QuotedIdentifier, bool IsDisabled);

internal sealed record SynonymInfo(string Schema, string Name, string BaseObjectName);

internal sealed record IdentityInfo(string Schema, string Table, decimal? LastValue, decimal Increment);

internal sealed record SequenceState(string Schema, string Name, string CurrentValue);

internal sealed record Binding(string Procedure, string ObjectName, string BoundName);

internal sealed record ExtendedProperty(
    string Name,
    object Value,
    string? Level0Type, string? Level0Name,
    string? Level1Type, string? Level1Name,
    string? Level2Type, string? Level2Name);

/// <summary>Reads everything about the source database that is taken from the catalog views rather than SMO.</summary>
internal sealed class SourceCatalog
{
    public required string Collation { get; init; }
    public required int CompatibilityLevel { get; init; }
    public required IReadOnlyList<string> Schemas { get; init; }
    public required IReadOnlyList<TableInfo> Tables { get; init; }
    public required IReadOnlyList<ModuleInfo> Modules { get; init; }
    public required IReadOnlyList<DatabaseTriggerInfo> DatabaseTriggers { get; init; }
    public required IReadOnlyList<SynonymInfo> Synonyms { get; init; }
    public required IReadOnlyList<IdentityInfo> Identities { get; init; }
    public required IReadOnlyList<SequenceState> Sequences { get; init; }
    public required IReadOnlyList<Binding> Bindings { get; init; }
    public required IReadOnlyList<ExtendedProperty> ExtendedProperties { get; init; }
    public required IReadOnlyList<string> Unsupported { get; init; }

    public static async Task<SourceCatalog> ReadAsync(SqlConnection connection, CancellationToken ct)
    {
        var (collation, compatibility) = await SingleAsync(connection,
            "SELECT collation_name, CAST(compatibility_level AS int) FROM sys.databases WHERE database_id = DB_ID()",
            r => (r.GetString(0), r.GetInt32(1)), ct);

        return new SourceCatalog
        {
            Collation = collation,
            CompatibilityLevel = compatibility,
            Schemas = await ListAsync(connection,
                "SELECT name FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383 ORDER BY schema_id",
                r => r.GetString(0), ct),
            Tables = await ListAsync(connection, """
                SELECT t.object_id, s.name, t.name
                FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE t.is_ms_shipped = 0 AND t.is_external = 0
                ORDER BY t.object_id
                """, r => new TableInfo(r.GetInt32(0), r.GetString(1), r.GetString(2)), ct),
            Modules = await ListAsync(connection, """
                SELECT o.object_id, s.name, o.name, RTRIM(o.type), m.definition,
                       m.uses_ansi_nulls, m.uses_quoted_identifier, ps.name, p.name,
                       CAST(ISNULL(tr.is_disabled, 0) AS bit)
                FROM sys.sql_modules m
                JOIN sys.objects o ON o.object_id = m.object_id
                JOIN sys.schemas s ON s.schema_id = o.schema_id
                LEFT JOIN sys.objects p ON p.object_id = o.parent_object_id AND o.parent_object_id <> 0
                LEFT JOIN sys.schemas ps ON ps.schema_id = p.schema_id
                LEFT JOIN sys.triggers tr ON tr.object_id = o.object_id
                WHERE o.is_ms_shipped = 0
                ORDER BY o.object_id
                """, r => new ModuleInfo(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetBoolean(5), r.GetBoolean(6),
                    r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
                    r.GetBoolean(9)), ct),
            DatabaseTriggers = await ListAsync(connection, """
                SELECT t.name, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier, t.is_disabled
                FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id
                WHERE t.parent_class = 0 AND t.is_ms_shipped = 0
                ORDER BY t.object_id
                """, r => new DatabaseTriggerInfo(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1),
                    r.GetBoolean(2), r.GetBoolean(3), r.GetBoolean(4)), ct),
            Synonyms = await ListAsync(connection, """
                SELECT s.name, sn.name, sn.base_object_name
                FROM sys.synonyms sn JOIN sys.schemas s ON s.schema_id = sn.schema_id
                WHERE sn.is_ms_shipped = 0
                """, r => new SynonymInfo(r.GetString(0), r.GetString(1), r.GetString(2)), ct),
            Identities = await ListAsync(connection, """
                SELECT s.name, t.name, CAST(ic.last_value AS decimal(38,0)), CAST(ic.increment_value AS decimal(38,0))
                FROM sys.identity_columns ic
                JOIN sys.tables t ON t.object_id = ic.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE t.is_ms_shipped = 0
                """, r => new IdentityInfo(r.GetString(0), r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetDecimal(2), r.GetDecimal(3)), ct),
            Sequences = await ListAsync(connection, """
                SELECT s.name, sq.name, CONVERT(nvarchar(50), sq.current_value)
                FROM sys.sequences sq JOIN sys.schemas s ON s.schema_id = sq.schema_id
                WHERE sq.is_ms_shipped = 0 AND sq.last_used_value IS NOT NULL
                """, r => new SequenceState(r.GetString(0), r.GetString(1), r.GetString(2)), ct),
            Bindings = await ReadBindingsAsync(connection, ct),
            ExtendedProperties = await ReadExtendedPropertiesAsync(connection, ct),
            Unsupported = await ListAsync(connection, """
                SELECT o.type_desc COLLATE DATABASE_DEFAULT + ' ' + QUOTENAME(s.name) + '.' + QUOTENAME(o.name)
                FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
                WHERE o.is_ms_shipped = 0 AND o.type IN ('PC', 'FS', 'FT', 'TA', 'AF', 'X')
                UNION ALL
                SELECT 'ASSEMBLY ' + QUOTENAME(name) FROM sys.assemblies WHERE is_user_defined = 1
                UNION ALL
                SELECT 'PARTITION SCHEME ' + QUOTENAME(name) FROM sys.partition_schemes
                UNION ALL
                SELECT 'FULLTEXT CATALOG ' + QUOTENAME(name) FROM sys.fulltext_catalogs
                """, r => r.GetString(0), ct),
        };
    }

    public static async Task<IReadOnlyList<ColumnInfo>> ReadCopyableColumnsAsync(SqlConnection connection, int objectId,
        CancellationToken ct)
    {
        // Computed, rowversion, GENERATED ALWAYS and graph pseudo-columns can't be inserted.
        await using var cmd = new SqlCommand("""
            SELECT c.name, ty.is_assembly_type, c.is_identity
            FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = @id
              AND c.is_computed = 0
              AND c.system_type_id <> 189
              AND c.generated_always_type = 0
              AND c.graph_type IS NULL
            ORDER BY c.column_id
            """, connection);
        cmd.Parameters.AddWithValue("@id", objectId);
        var columns = new List<ColumnInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add(new ColumnInfo(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        return columns;
    }

    private static async Task<IReadOnlyList<Binding>> ReadBindingsAsync(SqlConnection connection, CancellationToken ct)
    {
        // Legacy CREATE RULE / CREATE DEFAULT objects bound to types and columns (parent_object_id = 0 means the
        // default is a standalone object, not a default constraint). Types first, so explicit column bindings win.
        return await ListAsync(connection, """
            SELECT 'sys.sp_bindrule', QUOTENAME(ts.name) + '.' + QUOTENAME(t.name), QUOTENAME(rs.name) + '.' + QUOTENAME(r.name), 0
            FROM sys.types t JOIN sys.schemas ts ON ts.schema_id = t.schema_id
            JOIN sys.objects r ON r.object_id = t.rule_object_id JOIN sys.schemas rs ON rs.schema_id = r.schema_id
            WHERE t.is_user_defined = 1
            UNION ALL
            SELECT 'sys.sp_bindefault', QUOTENAME(ts.name) + '.' + QUOTENAME(t.name), QUOTENAME(ds.name) + '.' + QUOTENAME(d.name), 0
            FROM sys.types t JOIN sys.schemas ts ON ts.schema_id = t.schema_id
            JOIN sys.objects d ON d.object_id = t.default_object_id AND d.parent_object_id = 0
            JOIN sys.schemas ds ON ds.schema_id = d.schema_id
            WHERE t.is_user_defined = 1
            UNION ALL
            SELECT 'sys.sp_bindrule', QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + '.' + QUOTENAME(c.name),
                   QUOTENAME(rs.name) + '.' + QUOTENAME(r.name), 1
            FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.objects r ON r.object_id = c.rule_object_id JOIN sys.schemas rs ON rs.schema_id = r.schema_id
            WHERE o.is_ms_shipped = 0 AND o.type = 'U'
            UNION ALL
            SELECT 'sys.sp_bindefault', QUOTENAME(s.name) + '.' + QUOTENAME(o.name) + '.' + QUOTENAME(c.name),
                   QUOTENAME(ds.name) + '.' + QUOTENAME(d.name), 1
            FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id JOIN sys.schemas s ON s.schema_id = o.schema_id
            JOIN sys.objects d ON d.object_id = c.default_object_id AND d.parent_object_id = 0
            JOIN sys.schemas ds ON ds.schema_id = d.schema_id
            WHERE o.is_ms_shipped = 0 AND o.type = 'U'
            ORDER BY 4
            """, r => new Binding(r.GetString(0), r.GetString(1), r.GetString(2)), ct);
    }

    private static async Task<IReadOnlyList<ExtendedProperty>> ReadExtendedPropertiesAsync(SqlConnection connection,
        CancellationToken ct)
    {
        const string query = """
            SELECT ep.class, ep.name, ep.value,
                   os.name, o.name, RTRIM(o.type), ps.name, p.name, RTRIM(p.type),
                   c.name, prm.name, sch.name, i.name, ts.name, tt.name, ep.minor_id
            FROM sys.extended_properties ep
            LEFT JOIN sys.objects o ON ep.class IN (1, 2, 7) AND o.object_id = ep.major_id
            LEFT JOIN sys.schemas os ON os.schema_id = o.schema_id
            LEFT JOIN sys.objects p ON p.object_id = o.parent_object_id AND o.parent_object_id <> 0
            LEFT JOIN sys.schemas ps ON ps.schema_id = p.schema_id
            LEFT JOIN sys.columns c ON ep.class = 1 AND ep.minor_id > 0 AND c.object_id = ep.major_id AND c.column_id = ep.minor_id
            LEFT JOIN sys.parameters prm ON ep.class = 2 AND prm.object_id = ep.major_id AND prm.parameter_id = ep.minor_id
            LEFT JOIN sys.schemas sch ON ep.class = 3 AND sch.schema_id = ep.major_id
            LEFT JOIN sys.indexes i ON ep.class = 7 AND i.object_id = ep.major_id AND i.index_id = ep.minor_id
            LEFT JOIN sys.types tt ON ep.class = 6 AND tt.user_type_id = ep.major_id
            LEFT JOIN sys.schemas ts ON ts.schema_id = tt.schema_id
            WHERE ep.class IN (0, 1, 2, 3, 6, 7) AND ISNULL(o.is_ms_shipped, 0) = 0
            """;

        var result = new List<ExtendedProperty>();
        await using var cmd = new SqlCommand(query, connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var cls = r.GetByte(0);
            var name = r.GetString(1);
            var value = r.GetValue(2);
            string? objSchema = S(3), objName = S(4), objType = S(5), parentSchema = S(6), parentName = S(7),
                parentType = S(8), column = S(9), parameter = S(10), schema = S(11), index = S(12),
                typeSchema = S(13), typeName = S(14);
            var minorId = r.GetInt32(15);

            ExtendedProperty? ep = cls switch
            {
                0 => new(name, value, null, null, null, null, null, null),
                3 when schema is not null => new(name, value, "SCHEMA", schema, null, null, null, null),
                6 when typeName is not null => new(name, value, "SCHEMA", typeSchema, "TYPE", typeName, null, null),
                1 when objName is not null && parentName is not null && minorId == 0 =>
                    new(name, value, "SCHEMA", parentSchema, Level1Type(parentType!), parentName,
                        objType == "TR" ? "TRIGGER" : "CONSTRAINT", objName),
                1 when objName is not null && column is not null =>
                    new(name, value, "SCHEMA", objSchema, Level1Type(objType!), objName, "COLUMN", column),
                1 when objName is not null && minorId == 0 =>
                    new(name, value, "SCHEMA", objSchema, Level1Type(objType!), objName, null, null),
                2 when objName is not null && parameter is not null =>
                    new(name, value, "SCHEMA", objSchema, Level1Type(objType!), objName, "PARAMETER", parameter),
                7 when objName is not null && index is not null =>
                    new(name, value, "SCHEMA", objSchema, Level1Type(objType!), objName, "INDEX", index),
                _ => null,
            };
            if (ep is not null) result.Add(ep);
        }

        return result;
    }

    private static string Level1Type(string objectType) => objectType switch
    {
        "U" => "TABLE",
        "V" => "VIEW",
        "P" => "PROCEDURE",
        "FN" or "IF" or "TF" => "FUNCTION",
        "SN" => "SYNONYM",
        "SO" => "SEQUENCE",
        "R" => "RULE",
        "D" => "DEFAULT",
        _ => objectType,
    };

    public static async Task<T> SingleAsync<T>(SqlConnection connection, string sql, Func<SqlDataReader, T> map,
        CancellationToken ct)
    {
        var list = await ListAsync(connection, sql, map, ct);
        return list.Count == 1 ? list[0] : throw new CloneException($"Expected one row from: {sql}");
    }

    public static async Task<IReadOnlyList<T>> ListAsync<T>(SqlConnection connection, string sql,
        Func<SqlDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var list = new List<T>();
        while (await reader.ReadAsync(ct)) list.Add(map(reader));
        return list;
    }
}
