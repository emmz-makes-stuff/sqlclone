using System.CommandLine;

namespace SqlClone;

public static class Cli
{
    public const int Success = 0;
    public const int Failed = 1;
    public const int CompletedWithErrors = 2;

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null, TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        var source = new Option<string>("--source", "-s")
        {
            Description = "Connection string for the source database (must include the database name).",
            Required = true,
        };
        var target = new Option<string>("--target", "-t")
        {
            Description = "Connection string for the target server. Any database name in it is ignored.",
            Required = true,
        };
        var name = new Option<string>("--name", "-n")
        {
            Description = "Name to give the cloned database on the target server.",
            Required = true,
        };
        var force = new Option<bool>("--force", "-f")
        {
            Description = "Drop the target database first if it already exists.",
        };
        var parallelism = new Option<int>("--parallelism", "-p")
        {
            Description = "Number of tables to copy concurrently.",
            DefaultValueFactory = _ => 4,
        };

        var root = new RootCommand("Clone a SQL Server database, including all objects and data, to another server.")
        {
            source, target, name, force, parallelism,
        };

        root.SetAction(async (parseResult, ct) =>
        {
            var options = new CloneOptions(
                parseResult.GetValue(source)!,
                parseResult.GetValue(target)!,
                parseResult.GetValue(name)!,
                parseResult.GetValue(force),
                Math.Max(1, parseResult.GetValue(parallelism)));

            var log = new CloneLog(output, error);
            try
            {
                var result = await new DatabaseCloner(options, log).CloneAsync(ct);
                if (result.Errors.Count == 0)
                {
                    log.Info($"Cloned [{result.SourceDatabase}] to [{options.TargetDatabase}] in {result.Elapsed:g}.");
                    return Success;
                }

                log.Error($"Clone finished with {result.Errors.Count} error(s):");
                foreach (var e in result.Errors) log.Error($"  {e}");
                return CompletedWithErrors;
            }
            catch (CloneException ex)
            {
                log.Error(ex.Message);
                return Failed;
            }
            catch (Microsoft.Data.SqlClient.SqlException ex)
            {
                log.Error($"SQL Server error {ex.Number}: {ex.Message}");
                return Failed;
            }
            catch (OperationCanceledException)
            {
                log.Error("Cancelled.");
                return Failed;
            }
        });

        var config = new InvocationConfiguration { Output = output, Error = error };
        return await root.Parse(args).InvokeAsync(config, cancellationToken);
    }
}
