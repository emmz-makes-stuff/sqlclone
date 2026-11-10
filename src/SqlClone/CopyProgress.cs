using System.Diagnostics;
using System.Text;

namespace SqlClone;

/// <summary>
/// Live progress bars for the data copy: one for every row across all tables, and one for each table being copied.
/// Drawn with ANSI escape codes at the bottom of the terminal. Lines written through <see cref="CloneLog"/> while it's
/// live go above the bars.
/// </summary>
/// <remarks>
/// Row totals come from sys.partitions, so they're estimates: a table's percentage is capped at 99% until it finishes,
/// and when it does its actual count replaces the estimate in the overall total.
/// </remarks>
internal sealed class CopyProgress : IDisposable
{
    private const string HideCursor = "\e[?25l";
    private const string ShowCursor = "\e[?25h";
    private const int MaxNameWidth = 40;
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(100);

    private readonly TextWriter _output;
    private readonly CloneLog _log;
    private readonly IReadOnlyDictionary<int, long> _estimates;
    private readonly int _tableCount;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Timer _timer;

    // Guarded by the log's lock, which also serialises every write to the terminal.
    private readonly List<Active> _active = [];
    private long _totalRows;
    private long _finishedRows;
    private int _finishedTables;
    private int _drawnLines;
    private bool _disposed;

    public CopyProgress(TextWriter output, CloneLog log, IReadOnlyList<TableInfo> tables,
        IReadOnlyDictionary<int, long> estimates)
    {
        _output = output;
        _log = log;
        _estimates = estimates;
        _tableCount = tables.Count;
        _totalRows = tables.Sum(Estimate);

        _log.Attach(this);
        _log.Locked(() =>
        {
            _output.Write(HideCursor);
            Draw();
        });
        _timer = new Timer(_ => _log.Locked(Redraw), null, RedrawInterval, RedrawInterval);
    }

    /// <summary>Starts a table's bar; update it through the returned handle.</summary>
    public Active Start(TableInfo table)
    {
        var active = new Active(table, Estimate(table));
        _log.Locked(() => _active.Add(active));
        return active;
    }

    /// <summary>Removes a table's bar and adds its actual row count to the overall bar.</summary>
    public void Finish(Active active, long rows)
    {
        _log.Locked(() =>
        {
            _active.Remove(active);
            _totalRows += rows - active.Estimate;
            _finishedRows += rows;
            _finishedTables++;
        });
    }

    public void Dispose()
    {
        _timer.Dispose();
        _log.Locked(() =>
        {
            if (_disposed) return;
            _disposed = true;
            Clear();
            _output.Write(ShowCursor);
            _output.Flush();
        });
        _log.Attach(null);
    }

    /// <summary>Called by <see cref="CloneLog"/>, under its lock, before it writes a line.</summary>
    internal void Clear()
    {
        if (_drawnLines == 0) return;
        // Back to the start of the first bar, then clear to the end of the screen.
        _output.Write($"\e[{_drawnLines}F\e[J");
        _drawnLines = 0;
    }

    /// <summary>Called by <see cref="CloneLog"/>, under its lock, after it writes a line.</summary>
    internal void Draw()
    {
        if (_disposed) return;
        var lines = Render(TerminalWidth());
        var frame = new StringBuilder();
        foreach (var line in lines) frame.Append(line).Append('\n');
        _output.Write(frame.ToString());
        _output.Flush();
        _drawnLines = lines.Count;
    }

    private void Redraw()
    {
        if (_disposed) return;
        // One write per frame so the terminal never shows a half-cleared block.
        var lines = Render(TerminalWidth());
        var frame = new StringBuilder();
        if (_drawnLines > 0) frame.Append($"\e[{_drawnLines}F");
        foreach (var line in lines) frame.Append("\e[2K").Append(line).Append('\n');
        frame.Append("\e[J");
        _output.Write(frame.ToString());
        _output.Flush();
        _drawnLines = lines.Count;
    }

    /// <summary>The bar lines for a terminal <paramref name="width"/> columns wide; each fits without wrapping.</summary>
    internal IReadOnlyList<string> Render(int width)
    {
        width = Math.Max(1, width - 1);
        var lines = new List<string>(_active.Count + 1);

        var active = _active.Sum(a => a.Copied);
        var copied = _finishedRows + active;
        var total = Math.Max(_totalRows, copied);
        var elapsed = _elapsed.Elapsed;
        lines.Add(Line($"[{_finishedTables}/{_tableCount}] ", copied, total,
            $"  {Compact(copied)}/{Compact(total)} rows  {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}", width));

        var nameWidth = Math.Min(MaxNameWidth, _active.Select(a => a.Name.Length).DefaultIfEmpty(0).Max());
        foreach (var a in _active)
        {
            var copiedRows = a.Copied;
            var name = a.Name.Length > nameWidth ? a.Name[..(nameWidth - 1)] + "…" : a.Name.PadRight(nameWidth);
            lines.Add(Line($"  {name}  ", copiedRows, Math.Max(a.Estimate, copiedRows),
                $"  {Compact(copiedRows)}/{Compact(a.Estimate)}", width));
        }

        return lines;
    }

    private static string Line(string prefix, long copied, long total, string suffix, int width)
    {
        var fraction = total == 0 ? 0 : (double)copied / total;
        var percent = (int)(fraction * 100);
        if (percent >= 100) percent = 99;
        var label = $" {percent,3}%";

        var barWidth = width - prefix.Length - label.Length - suffix.Length;
        if (barWidth < 5)
        {
            // Too narrow for a bar: drop the counts, then the bar itself.
            suffix = "";
            barWidth = width - prefix.Length - label.Length;
        }

        if (barWidth < 5) return Fit(prefix + label.TrimStart(), width);

        var filled = (int)Math.Round(Math.Min(fraction, 1) * barWidth);
        return Fit(prefix + new string('█', filled) + new string('░', barWidth - filled) + label + suffix, width);
    }

    private static string Fit(string line, int width) => line.Length <= width ? line : line[..width];

    internal static string Compact(long n) => n switch
    {
        >= 1_000_000_000 => $"{n / 1e9:0.#}B",
        >= 1_000_000 => $"{n / 1e6:0.#}M",
        >= 10_000 => $"{n / 1e3:0}K",
        _ => n.ToString("N0"),
    };

    private long Estimate(TableInfo table) => _estimates.GetValueOrDefault(table.ObjectId);

    private static int TerminalWidth()
    {
        try
        {
            return Console.WindowWidth > 0 ? Console.WindowWidth : 80;
        }
        catch (IOException)
        {
            return 80;
        }
    }

    internal sealed class Active(TableInfo table, long estimate)
    {
        private long _copied;

        public string Name { get; } = table.QualifiedName;
        public long Estimate { get; } = estimate;
        public long Copied => Interlocked.Read(ref _copied);

        /// <summary>Sets how many rows have been copied so far; cheap enough to call on every row.</summary>
        public void Report(long rows) => Interlocked.Exchange(ref _copied, rows);
    }
}
