namespace SqlClone.IntegrationTests;

public sealed class CopyProgressTests
{
    private static readonly TableInfo Orders = new(1, "dbo", "Orders");
    private static readonly TableInfo Customers = new(2, "dbo", "Customers");
    private static readonly Dictionary<int, long> Estimates = new() { [1] = 1_000, [2] = 3_000 };

    [Fact]
    public void Renders_an_overall_bar_and_one_per_active_table()
    {
        using var progress = Create(new StringWriter(), out _);
        progress.Start(Orders).Report(500);
        progress.Start(Customers).Report(300);

        var lines = progress.Render(80);

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("[0/2] ", lines[0]);
        Assert.Contains(" 20%", lines[0]);
        Assert.Contains("800/4,000 rows", lines[0]);
        Assert.StartsWith("  [dbo].[Orders]     ", lines[1]);
        Assert.Contains(" 50%", lines[1]);
        Assert.Contains(" 10%", lines[2]);
        Assert.All(lines, l => Assert.True(l.Length < 80, $"'{l}' would wrap"));
    }

    [Fact]
    public void A_finished_table_replaces_its_estimate_with_its_actual_count()
    {
        using var progress = Create(new StringWriter(), out _);
        progress.Finish(progress.Start(Orders), rows: 1_500);

        var lines = progress.Render(80);

        Assert.Single(lines);
        Assert.StartsWith("[1/2] ", lines[0]);
        Assert.Contains("1,500/4,500 rows", lines[0]);
    }

    [Fact]
    public void Caps_a_table_at_99_percent_when_it_outruns_its_estimate()
    {
        using var progress = Create(new StringWriter(), out _);
        progress.Start(Orders).Report(2_000);

        Assert.Contains(" 99%", progress.Render(80)[1]);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(200)]
    public void Never_writes_a_line_wider_than_the_terminal(int width)
    {
        using var progress = Create(new StringWriter(), out _);
        progress.Start(new TableInfo(3, "a_very_long_schema_name", "and_an_even_longer_table_name_than_that")).Report(1);

        Assert.All(progress.Render(width), l => Assert.True(l.Length < width, $"'{l}' would wrap"));
    }

    [Fact]
    public void Writes_log_lines_above_the_bars_and_clears_them_when_done()
    {
        var output = new StringWriter();
        var progress = Create(output, out var log);

        log.Info("  [1/2] [dbo].[Orders]: 1,000 rows");
        progress.Dispose();

        var text = output.ToString();
        var line = text.IndexOf("[dbo].[Orders]: 1,000 rows", StringComparison.Ordinal);
        Assert.True(line > 0);
        Assert.Contains("\e[1F\e[J", text[..line]); // the bar drawn first was cleared before the line
        Assert.EndsWith("\e[1F\e[J\e[?25h", text); // and the bar drawn after it is cleared at the end
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(9_999, "9,999")]
    [InlineData(12_345, "12K")]
    [InlineData(1_240_000, "1.2M")]
    [InlineData(3_000_000_000, "3B")]
    public void Compacts_row_counts(long n, string expected) => Assert.Equal(expected, CopyProgress.Compact(n));

    private static CopyProgress Create(StringWriter output, out CloneLog log)
    {
        log = new CloneLog(output, new StringWriter());
        return new CopyProgress(output, log, [Orders, Customers], Estimates);
    }
}
