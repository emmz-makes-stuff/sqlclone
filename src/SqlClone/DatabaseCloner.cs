using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;

namespace SqlClone;

public sealed class DatabaseCloner(CloneOptions options, CloneLog log)
{
    public async Task<CloneResult> CloneAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var errors = new List<string>();
        var warnings = new List<string>();

        var sourceBuilder = new SqlConnectionStringBuilder(options.SourceConnectionString);
        var sourceDatabase = sourceBuilder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(sourceDatabase))
            throw new CloneException("The source connection string must specify a database (Initial Catalog / Database).");
        if (string.IsNullOrWhiteSpace(options.TargetDatabase))
            throw new CloneException("The target database name must not be empty.");

        var masterConnectionString = new SqlConnectionStringBuilder(options.TargetConnectionString)
            { InitialCatalog = "master" }.ConnectionString;
        var targetConnectionString = new SqlConnectionStringBuilder(options.TargetConnectionString)
            { InitialCatalog = options.TargetDatabase }.ConnectionString;

        await using var source = await OpenAsync(options.SourceConnectionString, "source", ct);
        log.Info($"Reading [{sourceDatabase}] catalog...");
        var catalog = await SourceCatalog.ReadAsync(source, ct);

        foreach (var item in catalog.Unsupported) Warn($"{item} is not supported and was not cloned.");
        foreach (var m in catalog.Modules.Where(m => m.Definition is null))
            Warn($"{m.Type} {Sql.Quote(m.Schema, m.Name)} is encrypted and was not cloned.");

        await using (var master = await OpenAsync(masterConnectionString, "target", ct))
        {
            await EnsureNotSameDatabaseAsync(source, master, sourceDatabase, ct);
            await CreateTargetDatabaseAsync(master, catalog, ct);
        }

        // SMO reads the source schema over its own connection so it can't interfere with ours.
        var smoConnection = new ServerConnection(new SqlConnection(options.SourceConnectionString));
        try
        {
            var scripter = new SchemaScripter(new Server(smoConnection).Databases[sourceDatabase], catalog);

            await using var target = await OpenAsync(targetConnectionString, "target", ct);

            log.Info("Scripting schema...");
            var preData = scripter.PreData();
            log.Info($"Creating {preData.Count} schema objects...");
            errors.AddRange(await ScriptExecutor.ExecuteAsync(target, preData, ct));

            log.Info($"Copying data for {catalog.Tables.Count} tables...");
            var copied = await new DataCopier(options.SourceConnectionString, targetConnectionString, options.Parallelism, log)
                .CopyAsync(catalog.Tables, ct);
            log.Info($"Copied {copied.Values.Sum():N0} rows.");

            var postData = scripter.PostData();
            log.Info($"Creating {postData.Count} indexes, constraints and triggers...");
            await ExecuteAsync(target, "SET ARITHABORT ON; SET NUMERIC_ROUNDABORT OFF; SET CONCAT_NULL_YIELDS_NULL ON;", ct);
            errors.AddRange(await ScriptExecutor.ExecuteAsync(target, postData, ct));

            await RestoreIdentitiesAsync(target, catalog, copied, errors, ct);
            await RestoreSequencesAsync(target, catalog, errors, ct);
            await CopyExtendedPropertiesAsync(target, catalog, errors, ct);
        }
        finally
        {
            smoConnection.Disconnect();
        }

        return new CloneResult(sourceDatabase, errors, warnings, stopwatch.Elapsed);

        void Warn(string message)
        {
            warnings.Add(message);
            log.Warn(message);
        }
    }

    private async Task EnsureNotSameDatabaseAsync(SqlConnection source, SqlConnection master, string sourceDatabase,
        CancellationToken ct)
    {
        if (!string.Equals(sourceDatabase, options.TargetDatabase, StringComparison.OrdinalIgnoreCase)) return;

        // tempdb is recreated whenever an instance starts, so its create_date tells two instances apart.
        const string instanceId = "SELECT CONCAT(@@SERVERNAME, '|', CONVERT(nvarchar(30), create_date, 126)) FROM sys.databases WHERE name = 'tempdb'";
        if (await ScalarAsync<string>(source, instanceId, ct) == await ScalarAsync<string>(master, instanceId, ct))
            throw new CloneException("The target database is the source database.");
    }

    private async Task CreateTargetDatabaseAsync(SqlConnection master, SourceCatalog catalog, CancellationToken ct)
    {
        var name = Sql.Quote(options.TargetDatabase);
        var exists = await ScalarAsync<object>(master, $"SELECT DB_ID({Sql.Literal(options.TargetDatabase)})", ct) is not DBNull;
        if (exists)
        {
            if (!options.Force)
                throw new CloneException($"Database {name} already exists on the target server. Use --force to replace it.");

            log.Info($"Dropping existing database {name}...");
            await ExecuteAsync(master, $"ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {name};", ct);
            SqlConnection.ClearAllPools();
        }

        log.Info($"Creating database {name}...");
        await ExecuteAsync(master, $"CREATE DATABASE {name} COLLATE {catalog.Collation}", ct);

        var maxLevel = await ScalarAsync<int>(master,
            "SELECT CAST(compatibility_level AS int) FROM sys.databases WHERE name = 'master'", ct);
        if (catalog.CompatibilityLevel <= maxLevel)
            await ExecuteAsync(master, $"ALTER DATABASE {name} SET COMPATIBILITY_LEVEL = {catalog.CompatibilityLevel}", ct);
    }

    private static async Task RestoreIdentitiesAsync(SqlConnection target, SourceCatalog catalog,
        IReadOnlyDictionary<TableInfo, long> copied, List<string> errors, CancellationToken ct)
    {
        var rowCounts = copied.ToDictionary(kv => (kv.Key.Schema, kv.Key.Name), kv => kv.Value);
        foreach (var identity in catalog.Identities.Where(i => i.LastValue is not null))
        {
            // On a table that has never had a row inserted, RESEED sets the value the *next* row gets.
            var empty = rowCounts.GetValueOrDefault((identity.Schema, identity.Table)) == 0;
            var value = empty ? identity.LastValue!.Value + identity.Increment : identity.LastValue!.Value;
            var table = Sql.Literal(Sql.Quote(identity.Schema, identity.Table));
            await TryAsync(target, $"DBCC CHECKIDENT ({table}, RESEED, {value}) WITH NO_INFOMSGS",
                $"identity on {Sql.Quote(identity.Schema, identity.Table)}", errors, ct);
        }
    }

    private static async Task RestoreSequencesAsync(SqlConnection target, SourceCatalog catalog, List<string> errors,
        CancellationToken ct)
    {
        foreach (var s in catalog.Sequences)
        {
            var name = Sql.Quote(s.Schema, s.Name);
            await TryAsync(target, $"ALTER SEQUENCE {name} RESTART WITH {s.CurrentValue}; SELECT NEXT VALUE FOR {name};",
                $"sequence {name}", errors, ct);
        }
    }

    private static async Task CopyExtendedPropertiesAsync(SqlConnection target, SourceCatalog catalog, List<string> errors,
        CancellationToken ct)
    {
        foreach (var ep in catalog.ExtendedProperties)
        {
            await using var cmd = new SqlCommand("sys.sp_addextendedproperty", target) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("@name", ep.Name);
            cmd.Parameters.Add(new SqlParameter("@value", SqlDbType.Variant) { Value = ep.Value });
            cmd.Parameters.AddWithValue("@level0type", (object?)ep.Level0Type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@level0name", (object?)ep.Level0Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@level1type", (object?)ep.Level1Type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@level1name", (object?)ep.Level1Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@level2type", (object?)ep.Level2Type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@level2name", (object?)ep.Level2Name ?? DBNull.Value);
            try
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex)
            {
                errors.Add($"extended property {ep.Name} on {ep.Level0Name}.{ep.Level1Name}.{ep.Level2Name}: {ex.Message}");
            }
        }
    }

    private static async Task TryAsync(SqlConnection connection, string sql, string description, List<string> errors,
        CancellationToken ct)
    {
        try
        {
            await ExecuteAsync(connection, sql, ct);
        }
        catch (SqlException ex)
        {
            errors.Add($"{description}: {ex.Message}");
        }
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, string role, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync();
            throw new CloneException($"Could not connect to the {role}: {ex.Message}", ex);
        }
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        return (T)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
