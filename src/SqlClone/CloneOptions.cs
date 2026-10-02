namespace SqlClone;

public sealed record CloneOptions(
    string SourceConnectionString,
    string TargetConnectionString,
    string TargetDatabase,
    bool Force = false,
    int Parallelism = 4);

public sealed record CloneResult(
    string SourceDatabase,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);

public sealed class CloneException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class CloneLog(TextWriter output, TextWriter error)
{
    private readonly Lock _lock = new();

    public void Info(string message)
    {
        lock (_lock) output.WriteLine(message);
    }

    public void Warn(string message)
    {
        lock (_lock) error.WriteLine($"warning: {message}");
    }

    public void Error(string message)
    {
        lock (_lock) error.WriteLine(message);
    }
}
