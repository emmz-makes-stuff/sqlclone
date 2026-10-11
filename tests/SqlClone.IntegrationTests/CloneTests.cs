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
    public async Task Use_existing_clones_alongside_what_the_database_already_has()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "UseExisting_Clone";
        var sourceDatabase = fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase);
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer, $"CREATE DATABASE [{cloneName}]", ct);
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName),
            "CREATE TABLE dbo.DUMMY (ID int); INSERT dbo.DUMMY VALUES (42);", ct);

        var (exitCode, output) = await RunAsync("-s", sourceDatabase, "-t", fixture.TargetServer, "-n", cloneName, "--use-existing");
        Assert.True(exitCode == Cli.Success, $"sqlclone exited with {exitCode}:\n{output}");
        Assert.Contains("Using existing database", output);

        await using (var connection = new SqlConnection(fixture.TargetDatabase(cloneName)))
        {
            await connection.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT ID FROM dbo.DUMMY", connection);
            Assert.Equal(42, await cmd.ExecuteScalarAsync(ct));
        }

        // With the table that was already there gone, the database is the clone and nothing else.
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName), "DROP TABLE dbo.DUMMY", ct);
        AssertSame(await DatabaseSnapshot.CaptureAsync(sourceDatabase, ct),
            await DatabaseSnapshot.CaptureAsync(fixture.TargetDatabase(cloneName), ct));
    }

    [Fact]
    public async Task Use_existing_refuses_a_name_the_clone_would_give_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "UseExisting_Clash";
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer, $"CREATE DATABASE [{cloneName}]", ct);
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName), "CREATE TABLE dbo.CUS_ENTITY_DATA (ID int)", ct);

        var (exitCode, output) = await RunAsync("-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase),
            "-t", fixture.TargetServer, "-n", cloneName, "--use-existing");
        Assert.Equal(Cli.Failed, exitCode);
        Assert.Contains("object [dbo].[CUS_ENTITY_DATA]", output);

        await using var connection = new SqlConnection(fixture.TargetDatabase(cloneName));
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0", connection);
        Assert.Equal(1, await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Use_existing_leaves_the_database_settings_alone_and_warns()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "UseExisting_Settings";
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer,
            $"CREATE DATABASE [{cloneName}] COLLATE Latin1_General_100_CS_AS; ALTER DATABASE [{cloneName}] SET COMPATIBILITY_LEVEL = 130;", ct);
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName), "CREATE TABLE dbo.DUMMY (ID int)", ct);

        var (exitCode, output) = await RunAsync("-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase),
            "-t", fixture.TargetServer, "-n", cloneName, "--use-existing", "--dry-run");
        Assert.True(exitCode == Cli.Success, $"sqlclone exited with {exitCode}:\n{output}");
        Assert.Contains("Did not change the collation", output);
        Assert.Contains("Did not change the compatibility level", output);
        Assert.Equal(("Latin1_General_100_CS_AS", 130), await SettingsAsync(fixture.TargetDatabase(cloneName), ct));
    }

    [Fact]
    public async Task Use_existing_needs_the_database_to_exist()
    {
        var (exitCode, output) = await RunAsync("-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase),
            "-t", fixture.TargetServer, "-n", "UseExisting_Missing", "--use-existing");
        Assert.Equal(Cli.Failed, exitCode);
        Assert.Contains("doesn't exist", output);
    }

    [Fact]
    public async Task Use_existing_and_force_cannot_be_used_together()
    {
        var (exitCode, output) = await RunAsync("-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase),
            "-t", fixture.TargetServer, "-n", "UseExisting_Force", "--use-existing", "--force");
        Assert.Equal(Cli.Failed, exitCode);
        Assert.Contains("can't be used together", output);
    }

    [Fact]
    public async Task Dry_run_reports_the_plan_and_creates_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "DryRun_Clone";

        var (exitCode, output) = await RunAsync("-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase),
            "-t", fixture.TargetServer, "-n", cloneName, "--dry-run");
        Assert.True(exitCode == Cli.Success, $"sqlclone exited with {exitCode}:\n{output}");
        Assert.Contains($"Would create database [{cloneName}]", output);
        Assert.Matches(@"Would create [1-9]\d* schema objects", output);
        Assert.Matches(@"Would copy [1-9]\d* tables", output);
        Assert.Contains("[dbo].[CUS_ENTITY_DATA]", output);
        Assert.Matches(@"Total: [1-9][\d,]* rows", output);

        await using var connection = new SqlConnection(fixture.TargetServer);
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand($"SELECT DB_ID('{cloneName}')", connection);
        Assert.IsType<DBNull>(await cmd.ExecuteScalarAsync(ct));
    }

    [Fact]
    public async Task Dry_run_refuses_a_database_that_is_not_empty_unless_forced_and_leaves_it_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        const string cloneName = "DryRun_Occupied";
        await SqlServerFixture.ExecuteAsync(fixture.TargetServer, $"CREATE DATABASE [{cloneName}]", ct);
        await SqlServerFixture.ExecuteAsync(fixture.TargetDatabase(cloneName), "CREATE TABLE dbo.MARKER (ID int)", ct);
        string[] args = ["-s", fixture.SourceDatabase(SqlServerFixture.CustomEntitiesDatabase), "-t", fixture.TargetServer,
            "-n", cloneName, "--dry-run"];

        var (exitCode, output) = await RunAsync(args);
        Assert.Equal(Cli.Failed, exitCode);
        Assert.Contains("isn't empty (object [dbo].[MARKER])", output);

        (exitCode, output) = await RunAsync([.. args, "--force"]);
        Assert.True(exitCode == Cli.Success, $"sqlclone exited with {exitCode}:\n{output}");
        Assert.Contains($"Would drop existing database [{cloneName}]", output);

        await using var connection = new SqlConnection(fixture.TargetDatabase(cloneName));
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("SELECT OBJECT_ID('dbo.MARKER')", connection);
        Assert.IsType<int>(await cmd.ExecuteScalarAsync(ct));
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
