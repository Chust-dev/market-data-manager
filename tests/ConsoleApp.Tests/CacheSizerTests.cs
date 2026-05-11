using HistoricalData.Manage;

namespace HistoricalData.Tests;

public sealed class CacheSizerTests : IDisposable
{
    private readonly string _root;

    public CacheSizerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CacheSizerTests_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>
    /// Creates a .bi5 file at pool/symbol/yyyy/mm/dd/HHh_ticks.bi5 of the
    /// requested size (filled with zero bytes; we only sum lengths, so the
    /// content is irrelevant).
    /// </summary>
    private string CreateTickFile(string symbol, int year, int dukaMonth, int day, int hour, int sizeBytes)
    {
        var dir = Path.Combine(_root, symbol, year.ToString("0000"), dukaMonth.ToString("00"), day.ToString("00"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{hour:00}h_ticks.bi5");
        File.WriteAllBytes(path, new byte[sizeBytes]);
        return path;
    }

    [Fact]
    public void Measure_SumsBytesPerSymbol()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 100);
        CreateTickFile("EURUSD", 2025, 0, 2, 11, 200);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, 50);

        var report = new CacheSizer(_root).Measure();

        Assert.Equal(350, report.TotalBytes);
        Assert.Equal(3, report.TotalFiles);
        Assert.Equal(2, report.Symbols.Count);

        var eu = report.Symbols.Single(s => s.Symbol == "EURUSD");
        Assert.Equal(300, eu.Bytes);
        Assert.Equal(2, eu.Files);

        var gbp = report.Symbols.Single(s => s.Symbol == "GBPUSD");
        Assert.Equal(50, gbp.Bytes);
        Assert.Equal(1, gbp.Files);
    }

    [Fact]
    public void Measure_BucketsBytesByYear()
    {
        CreateTickFile("EURUSD", 2024, 0, 2, 10, 100);
        CreateTickFile("EURUSD", 2024, 5, 15, 14, 200);
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 50);

        var report = new CacheSizer(_root).Measure();
        var eu = Assert.Single(report.Symbols);

        Assert.Equal(2, eu.ByYear.Count);
        Assert.Equal(300, eu.ByYear[2024].Bytes);
        Assert.Equal(2, eu.ByYear[2024].Files);
        Assert.Equal(50, eu.ByYear[2025].Bytes);
        Assert.Equal(1, eu.ByYear[2025].Files);
    }

    [Fact]
    public void Measure_SkipsNonSymbolDirectories()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 100);
        // Non-symbol neighbour with arbitrary content
        Directory.CreateDirectory(Path.Combine(_root, "Exports"));
        File.WriteAllText(Path.Combine(_root, "Exports", "some.csv"), "junk");

        var report = new CacheSizer(_root).Measure();

        Assert.Single(report.Symbols);
        Assert.Equal("EURUSD", report.Symbols[0].Symbol);
    }

    [Fact]
    public void Measure_RespectsSymbolFilter()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 100);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, 200);

        var report = new CacheSizer(_root).Measure(new[] { "EURUSD" });

        Assert.Single(report.Symbols);
        Assert.Equal("EURUSD", report.Symbols[0].Symbol);
    }

    [Fact]
    public void Measure_PoolMissing_ReturnsEmptyReport()
    {
        var sizer = new CacheSizer(Path.Combine(_root, "does-not-exist"));
        var report = sizer.Measure();

        Assert.False(report.PoolExists);
        Assert.Empty(report.Symbols);
        Assert.Equal(0, report.TotalBytes);
    }

    [Fact]
    public void Render_SortBySize_LargestFirst()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 100);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, 999);
        CreateTickFile("XAUUSD", 2025, 0, 2, 10, 50);

        var report = new CacheSizer(_root).Measure();
        var rendered = report.Render(CacheSizeSort.Size);

        // Order should be GBPUSD (999), EURUSD (100), XAUUSD (50).
        var gbp = rendered.IndexOf("GBPUSD", StringComparison.Ordinal);
        var eu = rendered.IndexOf("EURUSD", StringComparison.Ordinal);
        var xau = rendered.IndexOf("XAUUSD", StringComparison.Ordinal);
        Assert.True(gbp < eu && eu < xau,
            $"Expected GBPUSD < EURUSD < XAUUSD in output, got positions {gbp}/{eu}/{xau}");
    }

    [Fact]
    public void Render_SortBySymbol_Alphabetical()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 100);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, 999);
        CreateTickFile("AUDUSD", 2025, 0, 2, 10, 50);

        var report = new CacheSizer(_root).Measure();
        var rendered = report.Render(CacheSizeSort.Symbol);

        var aud = rendered.IndexOf("AUDUSD", StringComparison.Ordinal);
        var eu = rendered.IndexOf("EURUSD", StringComparison.Ordinal);
        var gbp = rendered.IndexOf("GBPUSD", StringComparison.Ordinal);
        Assert.True(aud < eu && eu < gbp);
    }

    [Fact]
    public void Render_SortByFiles_MostFirst()
    {
        // EURUSD: 3 files. GBPUSD: 1 file. XAUUSD: 2 files.
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 10);
        CreateTickFile("EURUSD", 2025, 0, 2, 11, 10);
        CreateTickFile("EURUSD", 2025, 0, 2, 12, 10);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, 10);
        CreateTickFile("XAUUSD", 2025, 0, 2, 10, 10);
        CreateTickFile("XAUUSD", 2025, 0, 2, 11, 10);

        var report = new CacheSizer(_root).Measure();
        var rendered = report.Render(CacheSizeSort.Files);

        var eu = rendered.IndexOf("EURUSD", StringComparison.Ordinal);
        var xau = rendered.IndexOf("XAUUSD", StringComparison.Ordinal);
        var gbp = rendered.IndexOf("GBPUSD", StringComparison.Ordinal);
        Assert.True(eu < xau && xau < gbp);
    }

    [Fact]
    public void RenderByYear_LongFormat_OneRowPerYear()
    {
        CreateTickFile("EURUSD", 2024, 0, 2, 10, 100);
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 200);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, 50);

        var report = new CacheSizer(_root).Measure();
        var rendered = report.RenderByYear(CacheSizeSort.Symbol);

        // Long format: three rows (EURUSD 2024, EURUSD 2025, GBPUSD 2025).
        Assert.Contains("2024", rendered);
        Assert.Contains("2025", rendered);
        // Symbol sort then year sort: EURUSD 2024 first, EURUSD 2025 second, GBPUSD 2025 third.
        var firstEu = rendered.IndexOf("EURUSD", StringComparison.Ordinal);
        var gbp = rendered.IndexOf("GBPUSD", StringComparison.Ordinal);
        Assert.True(firstEu < gbp);
    }

    [Fact]
    public void RenderByYear_SortBySize_LargestRowFirst()
    {
        CreateTickFile("EURUSD", 2024, 0, 2, 10, 100);
        CreateTickFile("EURUSD", 2025, 0, 2, 10, 999);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, 50);

        var report = new CacheSizer(_root).Measure();
        var rendered = report.RenderByYear(CacheSizeSort.Size);

        // The largest single (symbol, year) row is EURUSD 2025 = 999.
        // The data lines start with "  EURUSD" / "  GBPUSD" -- find the
        // first such line and check it's EURUSD with year 2025.
        var lines = rendered.Split('\n');
        var firstDataLine = lines.First(l => l.TrimStart().StartsWith("EURUSD") || l.TrimStart().StartsWith("GBPUSD"));
        Assert.Contains("EURUSD", firstDataLine);
        Assert.Contains("2025", firstDataLine);
    }

    [Fact]
    public void Render_EmptyPool_StillProducesHeader()
    {
        var report = new CacheSizer(_root).Measure();
        var rendered = report.Render(CacheSizeSort.Size);
        Assert.Contains("Pool:", rendered);
        Assert.Contains("Total size:", rendered);
    }

    [Fact]
    public void Render_PoolMissing_AnnouncesIt()
    {
        var report = new CacheSizer(Path.Combine(_root, "nope")).Measure();
        var rendered = report.Render(CacheSizeSort.Size);
        Assert.Contains("Pool path does not exist", rendered);
    }
}
