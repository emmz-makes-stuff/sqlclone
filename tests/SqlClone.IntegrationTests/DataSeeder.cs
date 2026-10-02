using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlClone.IntegrationTests;

/// <summary>
/// Fills every user table with random rows. The values mean nothing; they only need to fit the column types and
/// satisfy keys, so parents are seeded before children and foreign key columns take values from parent rows.
/// </summary>
internal sealed class DataSeeder(string connectionString, int seed)
{
    private readonly Random _random = new(seed);
    private int _stringCap = 40;
    private int _nullPercent = 10;

    private sealed record Column(string Name, string Type, int MaxLength, byte Precision, byte Scale, bool Nullable,
        bool Insertable, bool IsIdentity);

    private sealed record Table(int ObjectId, string Schema, string Name, List<Column> Columns)
    {
        public string Qualified => $"[{Schema}].[{Name}]";
    }

    private sealed record ForeignKey(int ObjectId, int Parent, int Referenced, List<(string Column, string ReferencedColumn)> Columns);

    public async Task SeedAsync(int minRows, int maxRows, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var tables = await ReadTablesAsync(connection, ct);
        var foreignKeys = await ReadForeignKeysAsync(connection, ct);
        var enabledTriggers = await ReadEnabledTriggersAsync(connection, ct);

        // Triggers in the samples call procedures that expect real data; they are not what's under test.
        foreach (var (table, trigger) in enabledTriggers)
            await ExecuteAsync(connection, $"DISABLE TRIGGER {trigger} ON {table}", ct);

        var parentKeys = new Dictionary<(int Table, string Columns), List<object?[]>>();
        foreach (var table in TopologicalOrder(tables, foreignKeys))
        {
            var fks = foreignKeys.Where(f => f.Parent == table.ObjectId && f.Referenced != table.ObjectId).ToList();
            var keyChoices = new List<(ForeignKey Fk, List<object?[]> Keys)>();
            foreach (var fk in fks)
            {
                var cacheKey = (fk.Referenced, string.Join(",", fk.Columns.Select(c => c.ReferencedColumn)));
                if (!parentKeys.TryGetValue(cacheKey, out var keys))
                {
                    keys = await ReadKeysAsync(connection, tables[fk.Referenced], fk, ct);
                    parentKeys[cacheKey] = keys;
                }

                keyChoices.Add((fk, keys));
            }

            var rows = _random.Next(minRows, maxRows + 1);
            for (var i = 0; i < rows; i++)
                await InsertRowAsync(connection, table, keyChoices, foreignKeys, i, ct);
        }

        foreach (var (table, trigger) in enabledTriggers)
            await ExecuteAsync(connection, $"ENABLE TRIGGER {trigger} ON {table}", ct);

        // Leave a gap at the end of each identity so the clone has to carry the identity value over, not just the rows.
        foreach (var table in tables.Values)
        {
            var identity = table.Columns.FirstOrDefault(c => c.IsIdentity);
            if (identity is null) continue;
            try
            {
                await ExecuteAsync(connection,
                    $"DELETE FROM {table.Qualified} WHERE [{identity.Name}] = (SELECT MAX([{identity.Name}]) FROM {table.Qualified})", ct);
            }
            catch (SqlException)
            {
                // Referenced by a child row; keep it.
            }
        }
    }

    private async Task InsertRowAsync(SqlConnection connection, Table table, List<(ForeignKey Fk, List<object?[]> Keys)> keyChoices,
        List<ForeignKey> allForeignKeys, int rowIndex, CancellationToken ct)
    {
        var columns = table.Columns.Where(c => c.Insertable).ToList();

        // Some tables have hundreds of string columns; keep their rows inside the 8,060 byte in-row limit.
        var variableWidth = columns.Count(c => c.Type is "varchar" or "nvarchar" or "varbinary");
        _stringCap = Math.Clamp(3_000 / Math.Max(1, variableWidth), 4, 40);
        _nullPercent = variableWidth > 300 ? 75 : 10;
        if (columns.Count == 0)
        {
            await TryInsertAsync(connection, $"INSERT INTO {table.Qualified} DEFAULT VALUES", [], ct);
            return;
        }

        var selfReferencing = allForeignKeys.Where(f => f.Parent == table.ObjectId && f.Referenced == table.ObjectId)
            .SelectMany(f => f.Columns.Select(c => c.Column)).ToHashSet();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var values = new Dictionary<string, object?>();
            foreach (var (fk, keys) in keyChoices)
            {
                var key = keys.Count == 0 ? null : keys[_random.Next(keys.Count)];
                for (var i = 0; i < fk.Columns.Count; i++) values[fk.Columns[i].Column] = key?[i];
            }

            foreach (var column in columns)
            {
                if (values.ContainsKey(column.Name)) continue;
                values[column.Name] = selfReferencing.Contains(column.Name) || (column.Nullable && _random.Next(100) < _nullPercent)
                    ? null
                    : RandomValue(column, rowIndex);
            }

            if (columns.Any(c => !c.Nullable && values[c.Name] is null)) return; // A required parent has no rows.

            // Nulls go in as literals: an untyped DBNull parameter is nvarchar, which won't convert to every type.
            var parameters = columns.Select((c, i) => new SqlParameter($"@p{i}", values[c.Name])).ToArray();
            var sql = $"INSERT INTO {table.Qualified} ({string.Join(", ", columns.Select(c => $"[{c.Name}]"))}) " +
                      $"VALUES ({string.Join(", ", parameters.Select(p => p.Value is null ? "NULL" : p.ParameterName))})";
            parameters = parameters.Where(p => p.Value is not null).ToArray();
            if (await TryInsertAsync(connection, sql, parameters, ct)) return;
        }
    }

    private static async Task<bool> TryInsertAsync(SqlConnection connection, string sql, SqlParameter[] parameters,
        CancellationToken ct)
    {
        try
        {
            await using var cmd = new SqlCommand(sql, connection);
            cmd.Parameters.AddRange(parameters);
            await cmd.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601 or 547 or 513 or 2628 or 8152 or 8115 or 220 or 242 or 511)
        {
            // Duplicate key, constraint/rule violation, truncation or overflow: roll again.
            return false;
        }
    }

    private object RandomValue(Column column, int rowIndex)
    {
        var r = _random;
        return column.Type switch
        {
            "bit" => r.Next(2) == 1,
            "tinyint" => (byte)r.Next(256),
            "smallint" => (short)r.Next(short.MaxValue),
            "int" => r.Next(1, 1_000_000),
            "bigint" => r.NextInt64(1, 1_000_000_000_000),
            "decimal" or "numeric" => RandomDecimal(column.Precision, column.Scale),
            "money" => Math.Round((decimal)r.NextDouble() * 1_000_000m, 4),
            "smallmoney" => Math.Round((decimal)r.NextDouble() * 200_000m, 4),
            "float" => r.NextDouble() * 1_000_000,
            "real" => (float)(r.NextDouble() * 1_000),
            "date" => new DateTime(2000, 1, 1).AddDays(r.Next(12_000)),
            "datetime" => new DateTime(2000, 1, 1).AddSeconds(r.NextInt64(900_000_000)).AddMilliseconds(r.Next(3) * 3 + r.Next(100) * 10),
            "smalldatetime" => new DateTime(2000, 1, 1).AddMinutes(r.Next(10_000_000)),
            "datetime2" => new DateTime(2000, 1, 1).AddTicks(r.NextInt64(9_000_000_000_000_000) / 10 * 10),
            "datetimeoffset" => new DateTimeOffset(new DateTime(2000, 1, 1).AddSeconds(r.NextInt64(900_000_000)), TimeSpan.FromHours(r.Next(-11, 13))),
            "time" => TimeSpan.FromSeconds(r.Next(86_400)),
            "uniqueidentifier" => Guid.NewGuid(),
            "char" or "varchar" or "text" => RandomString(column.MaxLength, rowIndex),
            "nchar" or "nvarchar" or "ntext" => RandomString(column.MaxLength < 0 ? -1 : column.MaxLength / 2, rowIndex),
            "binary" or "varbinary" or "image" => RandomBytes(column.MaxLength),
            "xml" => $"<value row=\"{rowIndex}\">{r.Next()}</value>",
            "sql_variant" => r.Next(),
            _ => throw new NotSupportedException($"No random value generator for {column.Type} ({column.Name})."),
        };
    }

    private decimal RandomDecimal(byte precision, byte scale)
    {
        var integerDigits = Math.Min(precision - scale, 12);
        var max = (decimal)Math.Pow(10, integerDigits);
        return Math.Round((decimal)_random.NextDouble() * max * 0.999m, Math.Min((int)scale, 6));
    }

    private string RandomString(int maxLength, int rowIndex)
    {
        // Leading with the row number keeps values in unique columns distinct where the column is wide enough.
        var limit = maxLength < 0 ? _stringCap : Math.Min(maxLength, _stringCap);
        var value = rowIndex + RandomLetters(_random.Next(1, 40));
        return value.Length > limit ? value[..limit] : value;
    }

    private string RandomLetters(int length) =>
        string.Create(length, _random, (span, r) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = (char)('A' + r.Next(26));
        });

    private byte[] RandomBytes(int maxLength)
    {
        var bytes = new byte[_random.Next(1, (maxLength < 0 ? _stringCap : Math.Min(maxLength, _stringCap)) + 1)];
        _random.NextBytes(bytes);
        return bytes;
    }

    private static IEnumerable<Table> TopologicalOrder(Dictionary<int, Table> tables, List<ForeignKey> foreignKeys)
    {
        var dependsOn = tables.Keys.ToDictionary(id => id, id => foreignKeys
            .Where(f => f.Parent == id && f.Referenced != id && tables.ContainsKey(f.Referenced))
            .Select(f => f.Referenced).ToHashSet());
        var done = new HashSet<int>();
        while (done.Count < tables.Count)
        {
            var ready = dependsOn.Where(kv => !done.Contains(kv.Key) && kv.Value.All(done.Contains)).Select(kv => kv.Key).ToList();
            // Break any cycle by taking the next table regardless; its FK columns get whatever parent rows exist.
            if (ready.Count == 0) ready = [dependsOn.Keys.First(id => !done.Contains(id))];
            foreach (var id in ready.Order())
            {
                done.Add(id);
                yield return tables[id];
            }
        }
    }

    private static async Task<Dictionary<int, Table>> ReadTablesAsync(SqlConnection connection, CancellationToken ct)
    {
        var tables = new Dictionary<int, Table>();
        await using var cmd = new SqlCommand("""
            SELECT t.object_id, s.name, t.name, c.name, TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale,
                   c.is_nullable, c.is_identity, c.is_computed, c.system_type_id, c.generated_always_type
            FROM sys.tables t
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.columns c ON c.object_id = t.object_id
            WHERE t.is_ms_shipped = 0
            ORDER BY t.object_id, c.column_id
            """, connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            if (!tables.TryGetValue(id, out var table))
                tables[id] = table = new Table(id, r.GetString(1), r.GetString(2), []);
            var systemType = r.GetByte(11);
            var insertable = !r.GetBoolean(9) && !r.GetBoolean(10) && systemType is not (189 or 240) && r.GetByte(12) == 0;
            table.Columns.Add(new Column(r.GetString(3), r.GetString(4), r.GetInt16(5), r.GetByte(6), r.GetByte(7),
                r.GetBoolean(8), insertable, r.GetBoolean(9)));
        }

        return tables;
    }

    private static async Task<List<ForeignKey>> ReadForeignKeysAsync(SqlConnection connection, CancellationToken ct)
    {
        var fks = new Dictionary<int, ForeignKey>();
        await using var cmd = new SqlCommand("""
            SELECT fk.object_id, fk.parent_object_id, fk.referenced_object_id, pc.name, rc.name
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            ORDER BY fk.object_id, fkc.constraint_column_id
            """, connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = r.GetInt32(0);
            if (!fks.TryGetValue(id, out var fk))
                fks[id] = fk = new ForeignKey(id, r.GetInt32(1), r.GetInt32(2), []);
            fk.Columns.Add((r.GetString(3), r.GetString(4)));
        }

        return fks.Values.ToList();
    }

    private static async Task<List<(string Table, string Trigger)>> ReadEnabledTriggersAsync(SqlConnection connection,
        CancellationToken ct)
    {
        var list = new List<(string, string)>();
        await using var cmd = new SqlCommand("""
            SELECT QUOTENAME(OBJECT_SCHEMA_NAME(parent_id)) + '.' + QUOTENAME(OBJECT_NAME(parent_id)),
                   QUOTENAME(OBJECT_SCHEMA_NAME(object_id)) + '.' + QUOTENAME(name)
            FROM sys.triggers WHERE parent_class = 1 AND is_disabled = 0
            """, connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetString(1)));
        return list;
    }

    private static async Task<List<object?[]>> ReadKeysAsync(SqlConnection connection, Table parent, ForeignKey fk,
        CancellationToken ct)
    {
        var columns = string.Join(", ", fk.Columns.Select(c => $"[{c.ReferencedColumn}]"));
        await using var cmd = new SqlCommand($"SELECT DISTINCT TOP (500) {columns} FROM {parent.Qualified}", connection);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var keys = new List<object?[]>();
        while (await r.ReadAsync(ct))
        {
            var key = new object?[fk.Columns.Count];
            for (var i = 0; i < key.Length; i++) key[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            if (key.All(k => k is not null)) keys.Add(key);
        }

        return keys;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
