using Microsoft.Data.SqlClient;

namespace SqlClone;

/// <summary>A named group of batches that is applied atomically.</summary>
internal sealed record ScriptUnit(string Description, IReadOnlyList<string> Batches);

internal static class ScriptExecutor
{
    /// <summary>
    /// Runs every unit in its own transaction. Units that fail are retried in later passes for as long as each pass
    /// makes progress, which takes care of dependency order between views, functions, computed columns and so on
    /// without having to build a dependency graph. Returns the units that never succeeded.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ExecuteAsync(SqlConnection connection, IReadOnlyList<ScriptUnit> units,
        CancellationToken ct)
    {
        var pending = units.ToList();
        var failures = new List<(ScriptUnit Unit, SqlException Error)>();

        while (pending.Count > 0)
        {
            failures.Clear();
            foreach (var unit in pending)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await ExecuteUnitAsync(connection, unit, ct);
                }
                catch (SqlException ex)
                {
                    failures.Add((unit, ex));
                }
            }

            if (failures.Count == pending.Count) break;
            pending = failures.Select(f => f.Unit).ToList();
        }

        return failures.Select(f => $"{f.Unit.Description}: {f.Error.Message}").ToList();
    }

    private static async Task ExecuteUnitAsync(SqlConnection connection, ScriptUnit unit, CancellationToken ct)
    {
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var batch in unit.Batches)
            {
                await using var cmd = new SqlCommand(batch, connection, tx) { CommandTimeout = 0 };
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            // The server may already have rolled the transaction back (e.g. on a batch-aborting error).
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
            throw;
        }
    }
}
