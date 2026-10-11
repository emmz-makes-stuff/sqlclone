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
        if (options.Force && options.UseExisting)
            throw new CloneException("--force and --use-existing can't be used together: one drops the target database, the other keeps what's in it.");

        var masterConnectionString = new SqlConnectionStringBuilder(options.TargetConnectionString)
            { InitialCatalog = "master" }.ConnectionString;
        var targetConnectionString = new SqlConnectionStringBuilder(options.TargetConnectionString)
            { InitialCatalog = options.TargetDatabase }.ConnectionString;

        if (options.DryRun) log.Info("Dry run: nothing will be written.");

        await using var source = await OpenAsync(options.SourceConnectionString, "source", ct);
        log.Info($"Reading [{sourceDatabase}] catalog...");
        var catalog = await SourceCatalog.ReadAsync(source, ct);

        foreach (var item in catalog.Unsupported) Warn($"{item} is not supported and was not cloned.");
        foreach (var m in catalog.Modules.Where(m => m.Definition is null))
            Warn($"{m.Type} {Sql.Quote(m.Schema, m.Name)} is encrypted and was not cloned.");

        await PrepareTargetDatabaseAsync(source, sourceDatabase, masterConnectionString, targetConnectionString, catalog,
            Warn, ct);

        // SMO reads the source schema over its own connection so it can't interfere with ours.
        var smoConnection = new ServerConnection(new SqlConnection(options.SourceConnectionString));
        try
        {
            var scripter = new SchemaScripter(new Server(smoConnection).Databases[sourceDatabase], catalog);

            if (options.DryRun)
            {
                await ReportPlanAsync(source, scripter, catalog, ct);
                return new CloneResult(sourceDatabase, errors, warnings, stopwatch.Elapsed);
            }

            await using var target = await OpenAsync(targetConnectionString, "target", ct);

            log.Info("Scripting schema...");
            var preData = scripter.PreData();
            log.Info($"Creating {preData.Count} schema objects...");
            errors.AddRange(await ScriptExecutor.ExecuteAsync(target, preData, ct));

            log.Info($"Copying data for {catalog.Tables.Count} tables...");
            var copied = await new DataCopier(options.SourceConnectionString, targetConnectionString, catalog.Features,
                    options.Parallelism, log)
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

    /// <summary>
    /// Creates the target database, or fills an existing one that's empty. An existing database that isn't empty is
    /// only replaced with --force, or filled alongside what it has with --use-existing.
    /// </summary>
    private async Task PrepareTargetDatabaseAsync(SqlConnection source, string sourceDatabase,
        string masterConnectionString, string targetConnectionString, SourceCatalog catalog, Action<string> warn,
        CancellationToken ct)
    {
        var name = Sql.Quote(options.TargetDatabase);

        // A login confined to one database (a contained user on Azure SQL, say) can't reach master. Then the database
        // is assumed to exist, and connecting to it tells.
        var (master, masterError) = await TryOpenAsync(masterConnectionString, ct);
        await using (master)
        {
            var exists = master is null ||
                         await ScalarAsync<object>(master, $"SELECT DB_ID({Sql.Literal(options.TargetDatabase)})", ct) is not DBNull;
            if (exists)
            {
                var (target, targetError) = await TryOpenAsync(targetConnectionString, ct);
                if (target is null)
                    throw master is null
                        ? new CloneException($"Could not connect to the target server's master database ({masterError!.Message}) or to database {name} ({targetError!.Message}).", targetError)
                        : new CloneException($"Database {name} already exists on the target server but could not be opened: {targetError!.Message}", targetError);

                await using (target)
                {
                    await EnsureNotSameDatabaseAsync(source, target, sourceDatabase, ct);

                    if (options.UseExisting)
                    {
                        await EnsureNoClashesAsync(source, target, ct);
                        log.Info(options.DryRun
                            ? $"Would use existing database {name}, leaving what is already in it."
                            : $"Using existing database {name}, leaving what is already in it...");
                        await MatchSourceSettingsAsync(target, catalog, warn, ct);
                        return;
                    }

                    var contents = await ListContentsAsync(target, ct);
                    if (contents.Count == 0)
                    {
                        log.Info(options.DryRun
                            ? $"Would use existing empty database {name}."
                            : $"Using existing empty database {name}...");
                        await MatchSourceSettingsAsync(target, catalog, warn, ct);
                        // Changing the collation leaves this session unusable, so it must not go back into the pool.
                        SqlConnection.ClearPool(target);
                        return;
                    }

                    if (!options.Force)
                        throw new CloneException($"Database {name} already exists on the target server and isn't empty ({string.Join(", ", contents)}). Use --force to replace it.");
                    if (master is null)
                        throw new CloneException($"Database {name} isn't empty, and replacing it needs the target server's master database: {masterError!.Message}", masterError);
                }

                if (options.DryRun)
                {
                    log.Info($"Would drop existing database {name}.");
                }
                else
                {
                    log.Info($"Dropping existing database {name}...");
                    await ExecuteAsync(master, $"ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE {name};", ct);
                    SqlConnection.ClearAllPools();
                }
            }
            else if (options.UseExisting)
            {
                throw new CloneException($"Database {name} doesn't exist on the target server, and --use-existing needs it to.");
            }

            await CreateTargetDatabaseAsync(master!, catalog, ct);
        }
    }

    private async Task EnsureNotSameDatabaseAsync(SqlConnection source, SqlConnection target, string sourceDatabase,
        CancellationToken ct)
    {
        if (!string.Equals(sourceDatabase, options.TargetDatabase, StringComparison.OrdinalIgnoreCase)) return;

        // The server name and creation time tell apart two databases with the same name.
        // Plain + rather than CONCAT, which a source older than SQL Server 2012 doesn't have.
        const string databaseId = "SELECT ISNULL(@@SERVERNAME, '') + '|' + CONVERT(nvarchar(30), create_date, 126) FROM sys.databases WHERE database_id = DB_ID()";
        if (await ScalarAsync<string>(source, databaseId, ct) == await ScalarAsync<string>(target, databaseId, ct))
            throw new CloneException("The target database is the source database.");
    }

    /// <summary>
    /// The named things in a database that the clone creates, each as the kind of name it is and the name, so that
    /// two that would clash read the same. Constraints and triggers on a table are left out unless asked for: the
    /// table says the database isn't empty, but their names still clash, since every object in a schema shares one
    /// namespace. A table type's hidden TT_ object belongs to the type. User schemas have ids from 5 (after dbo, guest,
    /// INFORMATION_SCHEMA and sys) to 16383 (before the role schemas).
    /// </summary>
    private static string ContentsQuery(bool childObjects) => $"""
        SELECT 'object ' + QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name) AS item FROM sys.objects
            WHERE is_ms_shipped = 0 AND type <> 'TT' {(childObjects ? "" : "AND parent_object_id = 0")}
        UNION ALL SELECT 'schema ' + QUOTENAME(name) FROM sys.schemas WHERE schema_id BETWEEN 5 AND 16383
        UNION ALL SELECT 'type ' + QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name) FROM sys.types
            WHERE is_user_defined = 1
        UNION ALL SELECT 'xml schema collection ' + QUOTENAME(SCHEMA_NAME(schema_id)) + '.' + QUOTENAME(name)
            FROM sys.xml_schema_collections WHERE schema_id <> SCHEMA_ID('sys')
        UNION ALL SELECT 'database trigger ' + QUOTENAME(name) FROM sys.triggers WHERE parent_class = 0
        """;

    /// <summary>Up to five of the things the clone would create that the database already has.</summary>
    private static Task<IReadOnlyList<string>> ListContentsAsync(SqlConnection target, CancellationToken ct) =>
        SourceCatalog.ListAsync(target, $"SELECT TOP (5) item FROM ({ContentsQuery(childObjects: false)}) AS contents ORDER BY item",
            r => r.GetString(0), ct);

    /// <summary>
    /// Stops a --use-existing clone before it writes anything if the database already has something with a name the
    /// clone would give.
    /// </summary>
    private async Task EnsureNoClashesAsync(SqlConnection source, SqlConnection target, CancellationToken ct)
    {
        var query = ContentsQuery(childObjects: true);
        // Ignoring case may find a clash that a case-sensitive target wouldn't have, but never misses one.
        var existing = (await SourceCatalog.ListAsync(target, query, r => r.GetString(0), ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clashes = (await SourceCatalog.ListAsync(source, query, r => r.GetString(0), ct))
            .Where(existing.Contains).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (clashes.Count > 0)
            throw new CloneException($"Database {Sql.Quote(options.TargetDatabase)} already has {clashes.Count} thing(s) with the same name as something the clone would create: {string.Join(", ", clashes.Take(10))}{(clashes.Count > 10 ? ", ..." : "")}");
    }

    /// <summary>
    /// Gives an existing database the source's collation and compatibility level, as a new one would get. A login
    /// that may not alter the database leaves them as they were, with a warning; so does --use-existing, which never
    /// alters the database itself.
    /// </summary>
    private async Task MatchSourceSettingsAsync(SqlConnection target, SourceCatalog catalog, Action<string> warn,
        CancellationToken ct)
    {
        var name = Sql.Quote(options.TargetDatabase);

        var collation = await ScalarAsync<string>(target, "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))", ct);
        if (!string.Equals(collation, catalog.Collation, StringComparison.OrdinalIgnoreCase))
        {
            // Needs the database to itself, which it has when nothing else is connected: this session can stay in it.
            if (!options.DryRun && !options.UseExisting) log.Info($"Changing the collation of {name} from {collation} to {catalog.Collation}...");
            await TryAsync($"ALTER DATABASE {name} COLLATE {catalog.Collation}",
                $"change the collation of {name} from {collation} to {catalog.Collation}");
        }

        var level = await ScalarAsync<int>(target, "SELECT CAST(compatibility_level AS int) FROM sys.databases WHERE database_id = DB_ID()", ct);
        if (level != catalog.CompatibilityLevel)
            await TryAsync($"ALTER DATABASE {name} SET COMPATIBILITY_LEVEL = {catalog.CompatibilityLevel}",
                $"change the compatibility level of {name} from {level} to {catalog.CompatibilityLevel}");

        // A dry run can't tell whether the login would be allowed to make the change.
        async Task TryAsync(string sql, string change)
        {
            if (options.UseExisting)
            {
                warn($"Did not {change}: --use-existing leaves the database's settings as they are.");
                return;
            }

            if (options.DryRun)
            {
                log.Info($"Would {change}.");
                return;
            }

            try
            {
                await ExecuteAsync(target, sql, ct);
            }
            catch (SqlException ex)
            {
                warn($"Could not {change}: {ex.Message}");
            }
        }
    }

    private async Task CreateTargetDatabaseAsync(SqlConnection master, SourceCatalog catalog, CancellationToken ct)
    {
        var name = Sql.Quote(options.TargetDatabase);
        var maxLevel = await ScalarAsync<int>(master,
            "SELECT CAST(compatibility_level AS int) FROM sys.databases WHERE name = 'master'", ct);
        var setLevel = catalog.CompatibilityLevel <= maxLevel;

        if (options.DryRun)
        {
            log.Info($"Would create database {name} with collation {catalog.Collation} and " +
                     (setLevel ? $"compatibility level {catalog.CompatibilityLevel}." : "the server's default compatibility level."));
            return;
        }

        log.Info($"Creating database {name}...");
        await ExecuteAsync(master, $"CREATE DATABASE {name} COLLATE {catalog.Collation}", ct);
        if (setLevel)
            await ExecuteAsync(master, $"ALTER DATABASE {name} SET COMPATIBILITY_LEVEL = {catalog.CompatibilityLevel}", ct);
    }

    /// <summary>Says what the clone would create and copy, from the scripts it would run and the source's row counts.</summary>
    private async Task ReportPlanAsync(SqlConnection source, SchemaScripter scripter, SourceCatalog catalog,
        CancellationToken ct)
    {
        log.Info("Scripting schema...");
        var preData = scripter.PreData().Count;
        var postData = scripter.PostData().Count;
        log.Info($"Would create {preData} schema objects, then {postData} indexes, constraints and triggers.");
        log.Info($"Would restore {catalog.Identities.Count(i => i.LastValue is not null)} identities and " +
                 $"{catalog.Sequences.Count} sequences, and copy {catalog.ExtendedProperties.Count} extended properties.");

        var rows = await SourceCatalog.ReadRowCountsAsync(source, ct);
        var width = catalog.Tables.Select(t => t.QualifiedName.Length).DefaultIfEmpty(0).Max();
        log.Info($"Would copy {catalog.Tables.Count} tables:");
        foreach (var t in catalog.Tables)
            log.Info($"  {t.QualifiedName.PadRight(width)} {rows.GetValueOrDefault(t.ObjectId),15:N0} rows");
        log.Info($"Total: {catalog.Tables.Sum(t => rows.GetValueOrDefault(t.ObjectId)):N0} rows.");
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

    private static async Task<(SqlConnection? Connection, SqlException? Error)> TryOpenAsync(string connectionString,
        CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return (connection, null);
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync();
            return (null, ex);
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
