using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SqlClone.IntegrationTests;

/// <summary>
/// Runs the sample scripts. They come from a tool that separates batches with a line holding only <c>;</c> rather
/// than <c>GO</c>, and doesn't always do even that, so a new batch is also started at every top-level CREATE of a
/// module (which must be first in its batch) - the diagram support objects are indented by one tab - and at every lower-case <c>create</c> the generator emits for tables
/// and indexes.
/// </summary>
internal static partial class SqlScript
{
    [GeneratedRegex(@"^\t?CREATE\s+(OR\s+ALTER\s+)?(PROC|PROCEDURE|FUNCTION|TRIGGER|VIEW|RULE|DEFAULT|SCHEMA)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleStart();

    [GeneratedRegex(@"^create\s")]
    private static partial Regex GeneratedCreate();

    [GeneratedRegex(@"^\s*(--.*)?$")]
    private static partial Regex BlankOrComment();

    public static IReadOnlyList<string> Split(string script)
    {
        var batches = new List<string>();
        var current = new StringBuilder();
        var hasContent = false;

        foreach (var line in script.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.Trim() == ";")
            {
                Flush();
                continue;
            }

            if (hasContent && (ModuleStart().IsMatch(line) || GeneratedCreate().IsMatch(line))) Flush();

            current.Append(line).Append('\n');
            hasContent |= !BlankOrComment().IsMatch(line);
        }

        Flush();
        return batches;

        void Flush()
        {
            if (hasContent) batches.Add(current.ToString());
            current.Clear();
            hasContent = false;
        }
    }

    /// <summary>
    /// Executes the batches, retrying failures for as long as each pass makes progress, because the samples are not
    /// in dependency order. Throws if anything still fails.
    /// </summary>
    public static async Task ExecuteAsync(string connectionString, string script, IReadOnlySet<int> ignoredErrors,
        CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var pending = Split(script).ToList();
        var failures = new List<(string Batch, string Error)>();
        while (pending.Count > 0)
        {
            failures.Clear();
            foreach (var batch in pending)
            {
                try
                {
                    await using var cmd = new SqlCommand(batch, connection) { CommandTimeout = 0 };
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException ex) when (!ignoredErrors.Contains(ex.Number))
                {
                    failures.Add((batch, ex.Message));
                }
                catch (SqlException)
                {
                    // A known defect in the sample; the object just won't exist.
                }
            }

            if (failures.Count == pending.Count) break;
            pending = failures.Select(f => f.Batch).ToList();
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException($"{failures.Count} batch(es) failed:\n" + string.Join("\n---\n",
                failures.Take(10).Select(f => $"{f.Error}\n{f.Batch[..Math.Min(f.Batch.Length, 400)]}")));
        }
    }
}
