using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

[assembly: AssemblyFixture(typeof(SqlClone.IntegrationTests.SqlServerFixture))]

namespace SqlClone.IntegrationTests;

/// <summary>
/// Two SQL Server 2025 containers: a source (Express edition, standing in for SQL Express) holding the two sample
/// databases filled with random data, and an empty target server to clone into.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2025-latest";

    public const string FrameworkDatabase = "SRM-Framework_145";
    public const string CustomEntitiesDatabase = "SRM-CustomEntities_145";

    private readonly MsSqlContainer _source = new MsSqlBuilder(Image).WithEnvironment("MSSQL_PID", "Express").Build();
    private readonly MsSqlContainer _target = new MsSqlBuilder(Image).Build();

    public string TargetServer => _target.GetConnectionString();

    public string SourceDatabase(string database) =>
        new SqlConnectionStringBuilder(_source.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    public string TargetDatabase(string database) =>
        new SqlConnectionStringBuilder(_target.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await Task.WhenAll(_source.StartAsync(ct), _target.StartAsync(ct));

        // The procedures in Framework refer to CustomEntities by three-part name, so create that one first.
        await CreateSourceAsync(CustomEntitiesDatabase, ["samples/SRM-CustomEntities.sql", "Scripts/Extras.sql"], null, ct);
        await CreateSourceAsync(FrameworkDatabase, ["samples/SRM-Framework.sql"],
            "CREATE USER usr_conversion WITHOUT LOGIN; CREATE USER usr_qasim WITHOUT LOGIN;", ct);

        // A disabled trigger is part of what has to survive the clone.
        await using var connection = new SqlConnection(SourceDatabase(CustomEntitiesDatabase));
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand("DISABLE TRIGGER audit.TR_EVENTS_NOOP ON audit.EVENTS; SELECT NEXT VALUE FOR audit.EVENT_NUMBERS;", connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Objects in SRM-Framework.sql that can't be created from the script here:
    // - 2714: GET_PIVOT_NEXT_ID is defined twice. The second copy is the renamed GET_PIVOT_NEXT_ID_BACKUP, whose stored
    //   text still has the old name, so 15151: the grant on GET_PIVOT_NEXT_ID_BACKUP fails too.
    // - 209: GET_HIDE_MODULE and GET_SHOW_MODULE don't compile ("Ambiguous column name").
    // - 7202: three procedures read tables in databases on linked servers (88.208.248.83, 10.0.0.100, db_server), and
    //   SQL Server checks remote objects when it compiles them.
    private static readonly IReadOnlySet<int> SampleDefects = new HashSet<int> { 2714, 209, 15151, 7202 };

    private async Task CreateSourceAsync(string database, string[] scripts, string? prerequisites, CancellationToken ct)
    {
        await ExecuteAsync(_source.GetConnectionString(), $"CREATE DATABASE [{database}]", ct);
        if (prerequisites is not null) await ExecuteAsync(SourceDatabase(database), prerequisites, ct);

        foreach (var script in scripts)
        {
            var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, script), ct);
            await SqlScript.ExecuteAsync(SourceDatabase(database), text, SampleDefects, ct);
        }

        await new DataSeeder(SourceDatabase(database), seed: database.Length).SeedAsync(minRows: 5, maxRows: 30, ct);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await _target.DisposeAsync();
    }
}
