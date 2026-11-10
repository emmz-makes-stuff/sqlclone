namespace SqlClone;

public sealed record CloneOptions(
    string SourceConnectionString,
    string TargetConnectionString,
    string TargetDatabase,
    bool Force = false,
    int Parallelism = 4,
    bool DryRun = false,
    bool UseExisting = false,
    bool Progress = false);

public sealed record CloneResult(
    string SourceDatabase,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);

public sealed class CloneException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class CloneLog(TextWriter output, TextWriter error)
{
    internal TextWriter Output => output;

    private readonly Lock _lock = new();
    private CopyProgress? _progress;

    public void Info(string message) => Write(output, message);

    public void Warn(string message) => Write(error, $"warning: {message}");

    public void Error(string message) => Write(error, message);

    /// <summary>While progress bars are live, lines are written above them.</summary>
    internal void Attach(CopyProgress? progress)
    {
        lock (_lock) _progress = progress;
    }

    /// <summary>Runs <paramref name="action"/> under the lock that serialises every write to the terminal.</summary>
    internal void Locked(Action action)
    {
        lock (_lock) action();
    }

    private void Write(TextWriter writer, string message)
    {
        lock (_lock)
        {
            _progress?.Clear();
            writer.WriteLine(message);
            if (_progress is null) return;
            writer.Flush();
            _progress.Draw();
        }
    }
}
