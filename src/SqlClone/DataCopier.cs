using System.Data;
using Microsoft.Data.SqlClient;

namespace SqlClone;

/// <summary>Streams every table from source to target with <see cref="SqlBulkCopy"/>.</summary>
internal sealed class DataCopier(string sourceConnectionString, string targetConnectionString, int parallelism, CloneLog log)
{
    private const SqlBulkCopyOptions BulkOptions =
        SqlBulkCopyOptions.KeepIdentity | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.TableLock;

    /// <summary>Copies all tables; returns the number of rows copied per table.</summary>
    public async Task<IReadOnlyDictionary<TableInfo, long>> CopyAsync(IReadOnlyList<TableInfo> tables, CancellationToken ct)
    {
        var copied = new System.Collections.Concurrent.ConcurrentDictionary<TableInfo, long>();
        var done = 0;

        await Parallel.ForEachAsync(tables, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (table, token) =>
            {
                var rows = await CopyTableAsync(table, token);
                copied[table] = rows;
                log.Info($"  [{Interlocked.Increment(ref done)}/{tables.Count}] {table.QualifiedName}: {rows:N0} rows");
            });

        return copied;
    }

    private async Task<long> CopyTableAsync(TableInfo table, CancellationToken ct)
    {
        await using var source = new SqlConnection(sourceConnectionString);
        await source.OpenAsync(ct);

        var columns = await SourceCatalog.ReadCopyableColumnsAsync(source, table.ObjectId, ct);
        if (columns.Count == 0) return 0;

        // CLR types (hierarchyid, geography, geometry) are moved as their binary serialisation so that the client
        // doesn't need the Microsoft.SqlServer.Types assembly.
        var select = "SELECT " + string.Join(", ", columns.Select(c => c.IsAssemblyType
            ? $"CAST({Sql.Quote(c.Name)} AS varbinary(max)) AS {Sql.Quote(c.Name)}"
            : Sql.Quote(c.Name))) + " FROM " + table.QualifiedName;

        await using var target = new SqlConnection(targetConnectionString);
        await target.OpenAsync(ct);

        try
        {
            await BulkCopyAsync(source, select, table, columns, ct);
        }
        catch (SqlException ex) when (ex.Number == 511)
        {
            // Bulk loads store every (max) value off-row behind a pointer, so a wide table of small (max) values that
            // fits row by row can't be bulk loaded at all. Fall back to INSERTs, which keep small values in-row.
            log.Warn($"{table.QualifiedName} is too wide to bulk load; copying it with INSERT statements instead.");
            await Execute(target, $"DELETE FROM {table.QualifiedName}", ct);
            await InsertCopyAsync(source, target, select, table, columns, ct);
        }

        await using var count = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table.QualifiedName}", target) { CommandTimeout = 0 };
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    private async Task BulkCopyAsync(SqlConnection source, string select, TableInfo table, IReadOnlyList<ColumnInfo> columns,
        CancellationToken ct)
    {
        await using var cmd = new SqlCommand(select, source) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);

        using var bulk = new SqlBulkCopy(targetConnectionString, BulkOptions)
        {
            DestinationTableName = table.QualifiedName,
            BulkCopyTimeout = 0,
            BatchSize = 50_000,
            EnableStreaming = true,
        };
        foreach (var c in columns) bulk.ColumnMappings.Add(c.Name, c.Name);

        await bulk.WriteToServerAsync(reader, ct);
    }

    private async Task InsertCopyAsync(SqlConnection source, SqlConnection target, string select, TableInfo table,
        IReadOnlyList<ColumnInfo> columns, CancellationToken ct)
    {
        const int rowsPerTransaction = 500;
        var identity = columns.Any(c => c.IsIdentity);

        // This path is slow, so report progress every 10%; the completion line comes from CopyAsync.
        long total;
        await using (var count = new SqlCommand($"SELECT COUNT_BIG(*) FROM {table.QualifiedName}", source) { CommandTimeout = 0 })
            total = (long)(await count.ExecuteScalarAsync(ct))!;
        var reported = 0;

        await using var cmd = new SqlCommand(select, source) { CommandTimeout = 0 };
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Parameters are typed from the source column so that NULLs and (max) values convert like the originals.
        // Every variable-length parameter needs an explicit size for the statement to be prepared.
        var schema = reader.GetSchemaTable()!.Rows;
        var insert = new SqlCommand(
            (identity ? $"SET IDENTITY_INSERT {table.QualifiedName} ON; " : "") +
            $"INSERT INTO {table.QualifiedName} ({string.Join(", ", columns.Select(c => Sql.Quote(c.Name)))}) " +
            $"VALUES ({string.Join(", ", columns.Select((_, i) => $"@p{i}"))});" +
            (identity ? $" SET IDENTITY_INSERT {table.QualifiedName} OFF;" : ""), target) { CommandTimeout = 0 };
        for (var i = 0; i < schema.Count; i++)
        {
            var column = schema[i];
            var parameter = insert.Parameters.Add($"@p{i}", (SqlDbType)(int)column["ProviderType"]);
            parameter.Size = (string)column["DataTypeName"] switch
            {
                "varchar" or "nvarchar" or "varbinary" or "xml" or "text" or "ntext" or "image" => -1,
                "char" or "nchar" or "binary" => (int)column["ColumnSize"],
                _ => parameter.Size,
            };
            if (column["NumericPrecision"] is short precision and < 255) parameter.Precision = (byte)precision;
            if (column["NumericScale"] is short scale and < 255) parameter.Scale = (byte)scale;
        }

        await using (insert)
        {
            // Preparing sends the (for a wide table, very long) statement once instead of on every row: ~5% faster
            // per row on a 1,012-column table.
            await insert.PrepareAsync(ct);

            long rows = 0;
            SqlTransaction? tx = null;
            try
            {
                while (await reader.ReadAsync(ct))
                {
                    tx ??= (SqlTransaction)await target.BeginTransactionAsync(ct);
                    insert.Transaction = tx;
                    for (var i = 0; i < schema.Count; i++) insert.Parameters[i].Value = reader.GetValue(i);
                    await insert.ExecuteNonQueryAsync(ct);
                    rows++;

                    var percent = total == 0 ? 0 : (int)(rows * 100 / total) / 10 * 10;
                    if (percent > reported && percent < 100)
                    {
                        reported = percent;
                        log.Info($"  {table.QualifiedName}: {percent}% ({rows:N0}/{total:N0} rows)");
                    }

                    if (rows % rowsPerTransaction != 0) continue;
                    await tx.CommitAsync(ct);
                    await tx.DisposeAsync();
                    tx = null;
                }

                if (tx is not null) await tx.CommitAsync(ct);
            }
            finally
            {
                if (tx is not null) await tx.DisposeAsync();
            }
        }
    }

    private static async Task Execute(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
