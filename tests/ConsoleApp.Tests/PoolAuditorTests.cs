using HistoricalData.Manage;

namespace HistoricalData.Tests;

public sealed class PoolAuditorTests : IDisposable
{
    private readonly string _root;

    public PoolAuditorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PoolAuditorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private void CreateTickFile(string symbol, int year, int dukaMonth, int day, int hour, int sizeBytes = 4096)
    {
        var dir = Path.Combine(_root, symbol, year.ToString("0000"), dukaMonth.ToString("00"), day.ToString("00"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{hour:00}h_ticks.bi5");
        File.WriteAllBytes(path, new byte[sizeBytes]);
    }

    private void CreateDailyM1File(string symbol, int year, int dukaMonth, int day, int sizeBytes = 8192)
    {
        var dir = Path.Combine(_root, symbol, year.ToString("0000"), dukaMonth.ToString("00"), day.ToString("00"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "BID_candles_min_1.bi5");
        File.WriteAllBytes(path, new byte[sizeBytes]);
    }

    [Fact]
    public void Audit_ReturnsEmptyReport_WhenPoolMissing()
    {
        var auditor = new PoolAuditor(Path.Combine(_root, "does-not-exist"));
        var report = auditor.Audit();
        Assert.False(report.PoolExists);
        Assert.Empty(report.Symbols);
    }

    [Fact]
    public void Audit_CountsHourTickFilesAndDailyM1Files()
    {
        // EURUSD with 3 hourly ticks and 1 daily M1 on 2025-01-02 (Dukascopy month 0)
        CreateTickFile("EURUSD", 2025, 0, 2, 10);
        CreateTickFile("EURUSD", 2025, 0, 2, 11);
        CreateTickFile("EURUSD", 2025, 0, 2, 12);
        CreateDailyM1File("EURUSD", 2025, 0, 2);

        var auditor = new PoolAuditor(_root);
        var report = auditor.Audit();

        Assert.True(report.PoolExists);
        var symbol = Assert.Single(report.Symbols);
        Assert.Equal("EURUSD", symbol.Symbol);
        Assert.Equal(3, symbol.HourTickFiles);
        Assert.Equal(1, symbol.DailyM1Files);
        Assert.Equal(0, symbol.EmptyFiles);
    }

    [Fact]
    public void Audit_DetectsEmptyFiles()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, sizeBytes: 0);
        CreateTickFile("EURUSD", 2025, 0, 2, 11, sizeBytes: 4096);

        var auditor = new PoolAuditor(_root);
        var report = auditor.Audit();

        var symbol = Assert.Single(report.Symbols);
        Assert.Equal(1, symbol.HourTickFiles); // valid file
        Assert.Equal(1, symbol.EmptyFiles);
    }

    [Fact]
    public void Audit_TracksFirstAndLastHour()
    {
        // Three ticks on different days, Dukascopy month 0 = January
        CreateTickFile("EURUSD", 2025, 0, 2, 10);
        CreateTickFile("EURUSD", 2025, 0, 5, 14);
        CreateTickFile("EURUSD", 2025, 0, 10, 23);

        var auditor = new PoolAuditor(_root);
        var report = auditor.Audit();
        var symbol = Assert.Single(report.Symbols);

        Assert.NotNull(symbol.FirstHour);
        Assert.NotNull(symbol.LastHour);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 10, 0, 0, TimeSpan.Zero), symbol.FirstHour);
        Assert.Equal(new DateTimeOffset(2025, 1, 10, 23, 0, 0, TimeSpan.Zero), symbol.LastHour);
    }

    [Fact]
    public void Audit_RespectsSymbolFilter()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10);
        CreateTickFile("XAUUSD", 2025, 0, 2, 10);

        var auditor = new PoolAuditor(_root);
        var report = auditor.Audit(new[] { "EURUSD", "XAUUSD" });

        Assert.Equal(2, report.Symbols.Count);
        Assert.DoesNotContain(report.Symbols, s => s.Symbol == "GBPUSD");
    }

    [Fact]
    public void Audit_ComputesPositiveCoverageRate()
    {
        // Fully covered single day: 24 hours × 1 day = 24 files
        for (var h = 0; h < 24; h++)
        {
            CreateTickFile("EURUSD", 2025, 0, 2, h);
        }

        var auditor = new PoolAuditor(_root);
        var report = auditor.Audit();
        var symbol = Assert.Single(report.Symbols);

        // First/last hour are ~24h apart, expected weekday share ~70%, we have 24/~17 → ratio is capped at 1.0
        Assert.True(symbol.CoverageRate > 0.0);
        Assert.True(symbol.CoverageRate <= 1.0);
        Assert.Equal(24, symbol.HourTickFiles);
    }

    [Fact]
    public void Render_ProducesPoolMissingMessage_WhenPoolDoesNotExist()
    {
        var report = new PoolAuditor(Path.Combine(_root, "missing")).Audit();
        var rendered = report.Render();
        Assert.Contains("Pool path does not exist", rendered);
    }

    [Fact]
    public void Render_IncludesPerSymbolRows()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10);

        var report = new PoolAuditor(_root).Audit();
        var rendered = report.Render();

        Assert.Contains("EURUSD", rendered);
        Assert.Contains("GBPUSD", rendered);
        Assert.Contains("Symbols cached: 2", rendered);
    }

    // ---------- Per-month / per-year coverage grid (BACKLOG #14) ----------

    [Fact]
    public void Audit_TracksHourTickFilesByMonth()
    {
        // EURUSD with 3 ticks in 2025-01 (Dukascopy month 0) and 1 tick in 2025-02.
        CreateTickFile("EURUSD", 2025, 0, 2, 10);
        CreateTickFile("EURUSD", 2025, 0, 2, 11);
        CreateTickFile("EURUSD", 2025, 0, 2, 12);
        CreateTickFile("EURUSD", 2025, 1, 5, 10);

        var report = new PoolAuditor(_root).Audit();
        var symbol = Assert.Single(report.Symbols);

        // Stored as calendar months (Dukascopy 0 == January, etc.)
        Assert.Equal(3, symbol.HourTickFilesIn(2025, 1));
        Assert.Equal(1, symbol.HourTickFilesIn(2025, 2));
        Assert.Equal(4, symbol.HourTickFilesIn(2025));
        Assert.Equal(0, symbol.HourTickFilesIn(2024)); // year not present
    }

    [Fact]
    public void CoveredYears_ReturnsDistinctSortedYears()
    {
        CreateTickFile("EURUSD", 2024, 0, 2, 10);
        CreateTickFile("EURUSD", 2025, 0, 2, 10);
        CreateTickFile("EURUSD", 2024, 5, 15, 10);

        var report = new PoolAuditor(_root).Audit();
        var symbol = Assert.Single(report.Symbols);

        Assert.Equal(new[] { 2024, 2025 }, symbol.CoveredYears().ToArray());
    }

    [Fact]
    public void RenderByYear_ShowsCoverageGrid()
    {
        // EURUSD: ticks in 2024 only. GBPUSD: ticks in 2024 and 2025.
        CreateTickFile("EURUSD", 2024, 0, 2, 10);
        CreateTickFile("GBPUSD", 2024, 0, 2, 10);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10);

        var rendered = new PoolAuditor(_root).Audit().RenderByYear();

        Assert.Contains("Year-by-year coverage:", rendered);
        Assert.Contains("EURUSD", rendered);
        Assert.Contains("GBPUSD", rendered);
        Assert.Contains("2024", rendered);
        Assert.Contains("2025", rendered);
        // EURUSD has no 2025 data so the cell should be `-`
        // (just check that the grid contains the dash sentinel somewhere)
        Assert.Contains("-", rendered);
    }

    [Fact]
    public void RenderByMonth_ShowsTwelveMonthsPerYear()
    {
        // Single tick in January (Dukascopy month 0 == calendar month 1).
        CreateTickFile("EURUSD", 2025, 0, 2, 10);

        var rendered = new PoolAuditor(_root).Audit().RenderByMonth();

        Assert.Contains("EURUSD month-by-month:", rendered);
        Assert.Contains("Jan", rendered);
        Assert.Contains("Dec", rendered);
        Assert.Contains("2025", rendered);
        // Months without files render as `-`
        Assert.Contains("-", rendered);
    }

    [Fact]
    public void RenderByMonth_HandlesEmptyPool()
    {
        var rendered = new PoolAuditor(_root).Audit().RenderByMonth();
        Assert.Contains("no hour-tick files", rendered);
    }

    [Fact]
    public void RenderByYear_HandlesEmptyPool()
    {
        var rendered = new PoolAuditor(_root).Audit().RenderByYear();
        Assert.Contains("no hour-tick files", rendered);
    }

    [Fact]
    public void ExpectedWeekdayHoursInMonth_CountsWeekdaysOnly()
    {
        // January 2025 has 31 days: 23 weekdays (Mon-Fri) + 8 weekend days (4 Sat + 4 Sun).
        var hours = PoolAuditor.ExpectedWeekdayHoursInMonth(2025, 1);
        Assert.Equal(23 * 24, hours);

        // February 2025 has 28 days. Feb 1 = Saturday, so 20 weekdays.
        Assert.Equal(20 * 24, PoolAuditor.ExpectedWeekdayHoursInMonth(2025, 2));
    }

    [Fact]
    public void ExpectedWeekdayHoursInYear_AggregatesAllMonths()
    {
        // 2024 was a leap year (366 days). 366/7 = 52 full weeks + 2 extra days.
        // 2024-01-01 was a Monday, so the +2 days are Mon+Tue (both weekdays).
        // 52 weeks × 5 weekdays + 2 = 262 weekdays × 24h = 6288h.
        Assert.Equal(262 * 24, PoolAuditor.ExpectedWeekdayHoursInYear(2024));
    }
}
