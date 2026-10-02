using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlClone.IntegrationTests;

/// <summary>
/// A comparable description of a database: one sorted list of lines per category, built from the catalog views and
/// from a hash of every table's rows. Two databases are clones of each other when every category matches.
/// </summary>
internal sealed class DatabaseSnapshot
{
    // SQL Server names some objects itself whenever their owner is created, so no clone can keep those names:
    // table types' internal tables (TT_<name>_<hex>, compared via sys.table_types instead), their constraints, and
    // constraints on the return table of a multi-statement table-valued function (PK__fn_Split__<hex>).
    private const string UserObject = "o.is_ms_shipped = 0 AND o.type <> 'TT' AND NOT EXISTS " +
        "(SELECT 1 FROM sys.table_types tt WHERE tt.type_table_object_id IN (o.object_id, o.parent_object_id)) " +
        "AND NOT EXISTS (SELECT 1 FROM sys.objects po WHERE po.object_id = o.parent_object_id AND po.type IN ('TF', 'IF', 'FT'))";

    private static readonly Dictionary<string, string> CatalogQueries = new()
    {
        ["database"] = """
            SELECT CONCAT('collation=', collation_name, ' compat=', compatibility_level) FROM sys.databases WHERE database_id = DB_ID()
            """,
        ["schemas"] = "SELECT name FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383",
        ["objects"] = $"""
            SELECT CONCAT(SCHEMA_NAME(o.schema_id), '.', o.name, ' ', o.type_desc COLLATE DATABASE_DEFAULT,
                          ' parent=', OBJECT_SCHEMA_NAME(o.parent_object_id), '.', OBJECT_NAME(o.parent_object_id))
            FROM sys.objects o WHERE {UserObject}
            """,
        ["modules"] = $"""
            SELECT CONCAT(SCHEMA_NAME(o.schema_id), '.', o.name, ' ansi_nulls=', m.uses_ansi_nulls,
                          ' quoted_identifier=', m.uses_quoted_identifier, ' schemabound=', m.is_schema_bound,
                          ' sha256=', CONVERT(varchar(64), HASHBYTES('SHA2_256', m.definition), 2))
            FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE {UserObject}
            UNION ALL
            SELECT CONCAT('database trigger ', t.name, ' sha256=', CONVERT(varchar(64), HASHBYTES('SHA2_256', m.definition), 2),
                          ' disabled=', t.is_disabled)
            FROM sys.triggers t JOIN sys.sql_modules m ON m.object_id = t.object_id WHERE t.parent_class = 0
            """,
        ["columns"] = $"""
            SELECT CONCAT(SCHEMA_NAME(o.schema_id), '.', o.name, '.', c.name, ' #', c.column_id, ' ',
                          SCHEMA_NAME(ty.schema_id), '.', ty.name, '(', c.max_length, ',', c.precision, ',', c.scale, ')',
                          ' null=', c.is_nullable, ' collation=', c.collation_name, ' rowguid=', c.is_rowguidcol,
                          ' identity=', CONVERT(nvarchar(50), ic.seed_value), ':', CONVERT(nvarchar(50), ic.increment_value),
                          ' computed=', cc.definition, ' persisted=', cc.is_persisted,
                          ' default=', dc.name, ':', dc.definition,
                          ' rule=', OBJECT_NAME(c.rule_object_id), ' bound_default=', IIF(dc.object_id IS NULL, OBJECT_NAME(c.default_object_id), NULL))
            FROM sys.columns c
            JOIN sys.objects o ON o.object_id = c.object_id
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE {UserObject}
            """,
        ["indexes"] = $"""
            SELECT CONCAT(SCHEMA_NAME(o.schema_id), '.', o.name, '.', i.name, ' ', i.type_desc COLLATE DATABASE_DEFAULT, ' unique=', i.is_unique,
                          ' pk=', i.is_primary_key, ' uc=', i.is_unique_constraint, ' filter=', i.filter_definition,
                          ' disabled=', i.is_disabled, ' fill=', i.fill_factor, ' padded=', i.is_padded,
                          ' ignore_dup=', i.ignore_dup_key, ' columns=',
                          (SELECT STRING_AGG(CONCAT(col.name, IIF(ic.is_descending_key = 1, ' DESC', ''), IIF(ic.is_included_column = 1, ' INCLUDE', '')), ',')
                                  WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, col.name)
                           FROM sys.index_columns ic JOIN sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
                           WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id),
                          ' compression=', (SELECT STRING_AGG(p.data_compression_desc COLLATE DATABASE_DEFAULT, ',') FROM sys.partitions p
                                            WHERE p.object_id = i.object_id AND p.index_id = i.index_id))
            FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id
            WHERE {UserObject} AND i.type > 0 AND o.type IN ('U', 'V')
            """,
        ["foreign keys"] = """
            SELECT CONCAT(OBJECT_SCHEMA_NAME(fk.parent_object_id), '.', OBJECT_NAME(fk.parent_object_id), '.', fk.name,
                          ' -> ', OBJECT_SCHEMA_NAME(fk.referenced_object_id), '.', OBJECT_NAME(fk.referenced_object_id),
                          ' delete=', fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT, ' update=', fk.update_referential_action_desc COLLATE DATABASE_DEFAULT,
                          ' disabled=', fk.is_disabled, ' untrusted=', fk.is_not_trusted, ' nfr=', fk.is_not_for_replication,
                          ' columns=', (SELECT STRING_AGG(CONCAT(pc.name, '=', rc.name), ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
                                        FROM sys.foreign_key_columns fkc
                                        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                                        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                                        WHERE fkc.constraint_object_id = fk.object_id))
            FROM sys.foreign_keys fk
            """,
        ["check constraints"] = $"""
            SELECT CONCAT(SCHEMA_NAME(o.schema_id), '.', OBJECT_NAME(o.parent_object_id), '.', o.name, ' ', cc.definition,
                          ' disabled=', cc.is_disabled, ' untrusted=', cc.is_not_trusted)
            FROM sys.check_constraints cc JOIN sys.objects o ON o.object_id = cc.object_id WHERE {UserObject}
            """,
        ["triggers"] = """
            SELECT CONCAT(OBJECT_SCHEMA_NAME(t.object_id), '.', t.name, ' on ', OBJECT_SCHEMA_NAME(t.parent_id), '.', OBJECT_NAME(t.parent_id),
                          ' disabled=', t.is_disabled, ' instead_of=', t.is_instead_of_trigger, ' events=',
                          (SELECT STRING_AGG(e.type_desc COLLATE DATABASE_DEFAULT, ',') WITHIN GROUP (ORDER BY e.type_desc COLLATE DATABASE_DEFAULT) FROM sys.trigger_events e WHERE e.object_id = t.object_id))
            FROM sys.triggers t
            """,
        ["types"] = """
            SELECT CONCAT(SCHEMA_NAME(t.schema_id), '.', t.name, ' base=', TYPE_NAME(t.system_type_id), '(', t.max_length, ',', t.precision,
                          ',', t.scale, ') null=', t.is_nullable, ' table=', t.is_table_type, ' rule=', OBJECT_NAME(t.rule_object_id),
                          ' default=', OBJECT_NAME(t.default_object_id))
            FROM sys.types t WHERE t.is_user_defined = 1
            UNION ALL
            SELECT CONCAT(SCHEMA_NAME(tt.schema_id), '.', tt.name, '.', c.name, ' #', c.column_id, ' ', TYPE_NAME(c.user_type_id),
                          '(', c.max_length, ',', c.precision, ',', c.scale, ') null=', c.is_nullable, ' identity=', c.is_identity)
            FROM sys.table_types tt JOIN sys.columns c ON c.object_id = tt.type_table_object_id
            """,
        ["synonyms"] = "SELECT CONCAT(SCHEMA_NAME(schema_id), '.', name, ' -> ', base_object_name) FROM sys.synonyms",
        ["sequences"] = """
            SELECT CONCAT(SCHEMA_NAME(schema_id), '.', name, ' ', TYPE_NAME(user_type_id), ' start=', CONVERT(nvarchar(50), start_value),
                          ' increment=', CONVERT(nvarchar(50), increment), ' min=', CONVERT(nvarchar(50), minimum_value),
                          ' max=', CONVERT(nvarchar(50), maximum_value), ' cycle=', is_cycling, ' cache=', is_cached, ':', cache_size,
                          ' current=', CONVERT(nvarchar(50), current_value))
            FROM sys.sequences
            """,
        ["identity values"] = $"""
            SELECT CONCAT(SCHEMA_NAME(o.schema_id), '.', o.name, ' current=', IDENT_CURRENT(QUOTENAME(SCHEMA_NAME(o.schema_id)) + '.' + QUOTENAME(o.name)))
            FROM sys.identity_columns ic JOIN sys.objects o ON o.object_id = ic.object_id WHERE {UserObject} AND o.type = 'U'
            """,
        ["extended properties"] = """
            SELECT CONCAT(ep.class_desc COLLATE DATABASE_DEFAULT, ' ',
                          CASE ep.class WHEN 1 THEN CONCAT(OBJECT_SCHEMA_NAME(ep.major_id), '.', OBJECT_NAME(ep.major_id), '.', COL_NAME(ep.major_id, ep.minor_id))
                                        WHEN 2 THEN CONCAT(OBJECT_SCHEMA_NAME(ep.major_id), '.', OBJECT_NAME(ep.major_id), '.',
                                                           (SELECT name FROM sys.parameters p WHERE p.object_id = ep.major_id AND p.parameter_id = ep.minor_id))
                                        WHEN 3 THEN SCHEMA_NAME(ep.major_id)
                                        WHEN 6 THEN TYPE_NAME(ep.major_id)
                                        WHEN 7 THEN CONCAT(OBJECT_SCHEMA_NAME(ep.major_id), '.', OBJECT_NAME(ep.major_id), '.',
                                                           (SELECT name FROM sys.indexes i WHERE i.object_id = ep.major_id AND i.index_id = ep.minor_id))
                          END,
                          ' ', ep.name, '=', CONVERT(nvarchar(max), ep.value), ' (', CONVERT(sysname, SQL_VARIANT_PROPERTY(ep.value, 'BaseType')), ')')
            FROM sys.extended_properties ep
            """,
    };

    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Categories { get; init; }

    /// <summary>Number of rows per table, for sanity checks on the seeded source.</summary>
    public required IReadOnlyDictionary<string, long> RowCounts { get; init; }

    public static async Task<DatabaseSnapshot> CaptureAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var categories = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (name, query) in CatalogQueries)
            categories[name] = await ReadLinesAsync(connection, query, ct);

        var (data, rowCounts) = await HashDataAsync(connection, ct);
        categories["data"] = data;
        return new DatabaseSnapshot { Categories = categories, RowCounts = rowCounts };
    }

    private static async Task<(IReadOnlyList<string> Lines, IReadOnlyDictionary<string, long> RowCounts)> HashDataAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var tables = new List<(string Name, List<string> Columns)>();
        await using (var cmd = new SqlCommand("""
            SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name), c.name, ty.is_assembly_type
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE t.is_ms_shipped = 0 AND c.system_type_id <> 189 -- rowversion values are regenerated by design
            ORDER BY t.object_id, c.column_id
            """, connection))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                var table = r.GetString(0);
                if (tables.Count == 0 || tables[^1].Name != table) tables.Add((table, []));
                var column = $"[{r.GetString(1).Replace("]", "]]")}]";
                tables[^1].Columns.Add(r.GetBoolean(2) ? $"CAST({column} AS varbinary(max))" : column);
            }
        }

        var lines = new List<string>();
        var counts = new Dictionary<string, long>();
        foreach (var (table, columns) in tables)
        {
            var rows = new List<string>();
            await using var cmd = new SqlCommand($"SELECT {string.Join(", ", columns)} FROM {table}", connection) { CommandTimeout = 0 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var values = new string[r.FieldCount];
            while (await r.ReadAsync(ct))
            {
                for (var i = 0; i < values.Length; i++) values[i] = Format(r.IsDBNull(i) ? null : r.GetValue(i));
                rows.Add(string.Join('\u001f', values));
            }

            rows.Sort(StringComparer.Ordinal);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', rows))));
            lines.Add($"{table} rows={rows.Count} sha256={hash}");
            counts[table] = rows.Count;
        }

        lines.Sort(StringComparer.Ordinal);
        return (lines, counts);
    }

    private static string Format(object? value) => value switch
    {
        null => "␀",
        byte[] bytes => Convert.ToHexString(bytes),
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static async Task<IReadOnlyList<string>> ReadLinesAsync(SqlConnection connection, string query, CancellationToken ct)
    {
        var lines = new List<string>();
        await using var cmd = new SqlCommand(query, connection) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) lines.Add(r.GetString(0));
        lines.Sort(StringComparer.Ordinal);
        return lines;
    }
}
