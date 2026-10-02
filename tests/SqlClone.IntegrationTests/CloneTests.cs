using Microsoft.Data.SqlClient;

namespace SqlClone.IntegrationTests;

public sealed class CloneTests(SqlServerFixture fixture)
{
    [Theory]
    [InlineData(SqlServerFixture.CustomEntitiesDatabase, "CustomEntities_Clone")]
    [InlineData(SqlServerFixture.FrameworkDatabase, "Framework_Clone")]
    public async Task Clones_all_objects_and_data(string sourceDatabase, string cloneName)
    {
        var ct = TestContext.Current.CancellationToken;

        var (exitCode, output) = await RunAsync("--source", fixture.SourceDatabase(sourceDatabase),
            "--target", fixture.TargetServer, "--name", cloneName);
        Assert.True(exitCode == Cli.Success, $"sqlclone exited with {exitCode}:\n{output}");

        var source = await DatabaseSnapshot.CaptureAsync(fixture.SourceDatabase(sourceDatabase), ct);
        var clone = await DatabaseSnapshot.CaptureAsync(fixture.TargetDatabase(cloneName), ct);

        // Guard against a vacuous pass: the seeded source must actually contain objects and data.
        Assert.NotEmpty(source.Categories["objects"]);
        Assert.NotEmpty(source.Categories["modules"]);
        var tablesWithData = source.RowCounts.Count(kv => kv.Value > 0);
        Assert.True(tablesWithData >= source.RowCounts.Count * 9 / 10,
            $"Only {tablesWithData} of {source.RowCounts.Count} source tables were seeded.");

        // CUS_ENTITY_DATA has 1,012 nvarchar(max) columns: too wide to bulk load, so it exercises the INSERT fallback.
        if (source.RowCounts.TryGetValue("[dbo].[CUS_ENTITY_DATA]", out var wideRows))
            Assert.True(wideRows > 0, "The wide table CUS_ENTITY_DATA was not seeded.");

        var differences = source.Categories.Keys
            .Select(category => Describe(category, source.Categories[category], clone.Categories[category]))
            .Where(d => d is not null)
            .ToList();
        Assert.True(differences.Count == 0, string.Join("\n\n", differences));
    }

    [Fact]
    public async Task Refuses_to_overwrite_an_existing_database_unless_forced()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] args = ["-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase), "-t", fixture.TargetServer, "-n", "Force_Clone"];

        Assert.Equal(Cli.Success, (await RunAsync(args)).ExitCode);

        await using (var connection = new SqlConnection(fixture.TargetDatabase("Force_Clone")))
        {
            await connection.OpenAsync(ct);
            await using var cmd = new SqlCommand("CREATE TABLE dbo.MARKER (ID int)", connection);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var (exitCode, output) = await RunAsync(args);
        Assert.Equal(Cli.Failed, exitCode);
        Assert.Contains("already exists", output);

        Assert.Equal(Cli.Success, (await RunAsync([.. args, "--force"])).ExitCode);
        await using (var connection = new SqlConnection(fixture.TargetDatabase("Force_Clone")))
        {
            await connection.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT OBJECT_ID('dbo.MARKER')", connection);
            Assert.IsType<DBNull>(await cmd.ExecuteScalarAsync(ct));
        }
    }

    [Fact]
    public async Task Requires_a_source_database_name()
    {
        var sourceServer = new SqlConnectionStringBuilder(fixture.SourceDatabase("x")) { InitialCatalog = "" }.ConnectionString;
        var (exitCode, output) = await RunAsync("-s", sourceServer, "-t", fixture.TargetServer, "-n", "Nameless");
        Assert.Equal(Cli.Failed, exitCode);
        Assert.Contains("must specify a database", output);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var exitCode = await Cli.RunAsync(args, output, output, TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper?.WriteLine(output.ToString());
        return (exitCode, output.ToString());
    }

    private static string? Describe(string category, IReadOnlyList<string> source, IReadOnlyList<string> clone)
    {
        var missing = source.Except(clone, StringComparer.Ordinal).ToList();
        var extra = clone.Except(source, StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && extra.Count == 0 && source.Count == clone.Count) return null;

        return $"{category}: {missing.Count} missing from clone, {extra.Count} unexpected in clone\n" +
               string.Join("\n", missing.Take(15).Select(m => $"  - {m}")) + "\n" +
               string.Join("\n", extra.Take(15).Select(e => $"  + {e}"));
    }
}
