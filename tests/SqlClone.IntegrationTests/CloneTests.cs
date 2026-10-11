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

        AssertSame(source, clone);
    }

    [Fact]
    public async Task Clones_into_an_existing_empty_database_and_gives_it_the_source_settings()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "Existing_Clone";
        var sourceDatabase = fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase);
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer,
            $"CREATE DATABASE [{cloneName}] COLLATE Latin1_General_100_CS_AS; ALTER DATABASE [{cloneName}] SET COMPATIBILITY_LEVEL = 130;", ct);

        var (exitCode, output) = await RunAsync("-s", sourceDatabase, "-t", fixture.TargetServer, "-n", cloneName);
        Assert.True(exitCode == Cli.Success, $"sqlclone exited with {exitCode}:\n{output}");
        Assert.Contains("Using existing empty database", output);

        Assert.Equal(await SettingsAsync(sourceDatabase, ct), await SettingsAsync(fixture.TargetDatabase(cloneName), ct));
        AssertSame(await DatabaseSnapshot.CaptureAsync(sourceDatabase, ct),
            await DatabaseSnapshot.CaptureAsync(fixture.TargetDatabase(cloneName), ct));
    }

    [Fact]
    public async Task Clones_into_an_existing_database_as_a_user_who_cannot_reach_master()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "Contained_Clone";
        const string password = "C0nt@ined-Us3r";
        var sourceDatabase = fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase);
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer,
            $"EXEC sp_configure 'contained database authentication', 1; RECONFIGURE; CREATE DATABASE [{cloneName}] CONTAINMENT = PARTIAL;", ct);
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName),
            $"CREATE USER cloner WITH PASSWORD = '{password}'; ALTER ROLE db_owner ADD MEMBER cloner;", ct);

        var contained = new SqlConnectionStringBuilder(fixture.TargetServer) { UserID = "cloner", Password = password }.ConnectionString;
        var (exitCode, output) = await RunAsync("-s", sourceDatabase, "-t", contained, "-n", cloneName);
        Assert.True(exitCode == Cli.Success, $"sqlclone exited with {exitCode}:\n{output}");

        // Dropping the user also proves it owns nothing it created. A contained database's metadata has its own collation, which the snapshot queries can't mix with the
        // database's, so it goes back to an ordinary database before it is compared.
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName), "DROP USER cloner", ct);
        SqlConnection.ClearAllPools();
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer,
            $"ALTER DATABASE [{cloneName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ALTER DATABASE [{cloneName}] SET CONTAINMENT = NONE; ALTER DATABASE [{cloneName}] SET MULTI_USER;", ct);
        AssertSame(await DatabaseSnapshot.CaptureAsync(sourceDatabase, ct),
            await DatabaseSnapshot.CaptureAsync(fixture.TargetDatabase(cloneName), ct));
    }

    [Fact]
    public async Task Refuses_an_existing_database_that_is_not_empty_and_says_what_is_in_it()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "Occupied_Clone";
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer, $"CREATE DATABASE [{cloneName}]", ct);
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName), "CREATE TABLE dbo.MARKER (ID int)", ct);

        var (exitCode, output) = await RunAsync("-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase),
            "-t", fixture.TargetServer, "-n", cloneName);
        Assert.Equal(Cli.Failed, exitCode);
        Assert.Contains("isn't empty (object [dbo].[MARKER])", output);
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

    private static async Task<(string Collation, int CompatibilityLevel)> SettingsAsync(string connectionString,
        CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand(
            "SELECT collation_name, CAST(compatibility_level AS int) FROM sys.databases WHERE database_id = DB_ID()", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static void AssertSame(DatabaseSnapshot source, DatabaseSnapshot clone)
    {
        var differences = source.Categories.Keys
            .Select(category => Describe(category, source.Categories[category], clone.Categories[category]))
            .Where(d => d is not null)
            .ToList();
        Assert.True(differences.Count == 0, string.Join("\n\n", differences));
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
