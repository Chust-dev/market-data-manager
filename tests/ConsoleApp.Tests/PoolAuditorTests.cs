using HistoricalData.Audit;

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
}
