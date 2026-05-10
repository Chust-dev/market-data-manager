using HistoricalData.DataPool;
using HistoricalData.Manage;

namespace HistoricalData.Tests;

public sealed class CacheRepairerTests : IDisposable
{
    private readonly string _root;

    public CacheRepairerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CacheRepairerTests_" + Guid.NewGuid().ToString("N"));
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

    private string CreateTickFile(string symbol, int year, int dukaMonth, int day, int hour, byte[] bytes, bool writeMeta = true)
    {
        var dir = Path.Combine(_root, symbol, year.ToString("0000"), dukaMonth.ToString("00"), day.ToString("00"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{hour:00}h_ticks.bi5");
        File.WriteAllBytes(path, bytes);
        if (writeMeta)
        {
            DataPoolFileMeta.Write(path);
        }
        return path;
    }

    /// <summary>
    /// Test double for IFileRefetcher. Records every call and lets the test
    /// decide what each refetch should do: succeed (overwriting the cache file
    /// with canned bytes and writing a fresh sidecar), or fail (untouched file).
    /// </summary>
    private sealed class FakeFileRefetcher : IFileRefetcher
    {
        public List<(string Symbol, int Year, int Month, int Day, string FileName)> Calls { get; } = new();
        public Func<string, byte[]?> CannedBytes { get; set; } = _ => new byte[] { 99, 99, 99, 99 };
        public string PoolRoot { get; init; } = string.Empty;

        public Task<bool> RefetchAsync(string symbol, int year, int dukaMonth, int day, string fileName, CancellationToken cancellationToken)
        {
            Calls.Add((symbol, year, dukaMonth, day, fileName));

            var bytes = CannedBytes(fileName);
            if (bytes is null)
            {
                return Task.FromResult(false);
            }

            var path = Path.Combine(
                PoolRoot,
                symbol,
                year.ToString("0000"),
                dukaMonth.ToString("00"),
                day.ToString("00"),
                fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            DataPoolFileMeta.Write(path);
            return Task.FromResult(true);
        }
    }

    private FakeFileRefetcher NewRefetcher() => new() { PoolRoot = _root };

    [Fact]
    public async Task Repair_SkipsCleanFiles()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        CreateTickFile("EURUSD", 2025, 0, 2, 11, new byte[] { 5, 6, 7, 8 });

        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(_root, refetcher);
        var report = await repairer.RepairAsync(symbolFilter: null, dryRun: false, trustExisting: false);

        Assert.Equal(2, report.FilesPlanned);
        Assert.Equal(0, report.ProblemsFound);
        Assert.Empty(refetcher.Calls);
    }

    [Fact]
    public async Task Repair_NoMetadata_RefetchesByDefault()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);

        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(_root, refetcher);
        var report = await repairer.RepairAsync(symbolFilter: null, dryRun: false, trustExisting: false);

        Assert.Equal(1, report.ProblemsFound);
        Assert.Equal(1, report.Refetched);
        Assert.Equal(0, report.SidecarRegenerated);
        Assert.Single(refetcher.Calls);
        Assert.True(report.AllResolved);
    }

    [Fact]
    public async Task Repair_NoMetadata_TrustExisting_RegeneratesSidecarOnly()
    {
        var path = CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);

        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(_root, refetcher);
        var report = await repairer.RepairAsync(symbolFilter: null, dryRun: false, trustExisting: true);

        Assert.Equal(1, report.ProblemsFound);
        Assert.Equal(1, report.SidecarRegenerated);
        Assert.Equal(0, report.Refetched);
        Assert.Empty(refetcher.Calls); // no network — trust-existing must not call the refetcher
        Assert.True(File.Exists(path + ".meta.json"));
        Assert.True(report.AllResolved);
    }

    [Fact]
    public async Task Repair_HashMismatch_AlwaysRefetches()
    {
        var path = CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        // Tamper with the file so its hash no longer matches the sidecar.
        File.WriteAllBytes(path, new byte[] { 9, 9, 9, 9 });

        var refetcher = NewRefetcher();
        // Have the refetcher restore the original bytes.
        refetcher.CannedBytes = _ => new byte[] { 1, 2, 3, 4 };
        var repairer = new CacheRepairer(_root, refetcher);
        var report = await repairer.RepairAsync(symbolFilter: null, dryRun: false, trustExisting: true);

        Assert.Equal(1, report.ProblemsFound);
        Assert.Equal(1, report.Refetched);
        Assert.Single(refetcher.Calls);
        Assert.Equal(("EURUSD", 2025, 0, 2, "10h_ticks.bi5"), refetcher.Calls[0]);
        Assert.True(report.AllResolved);
    }

    [Fact]
    public async Task Repair_RefetchFails_LeavesOriginalUntouched()
    {
        var path = CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(path, new byte[] { 9, 9, 9, 9 });

        var refetcher = NewRefetcher();
        refetcher.CannedBytes = _ => null; // simulate 404 / network failure
        var repairer = new CacheRepairer(_root, refetcher);
        var report = await repairer.RepairAsync(symbolFilter: null, dryRun: false, trustExisting: false);

        Assert.Equal(1, report.ProblemsFound);
        Assert.Equal(0, report.Refetched);
        Assert.Equal(1, report.RefetchFailed);
        Assert.False(report.AllResolved);

        // Original (tampered) bytes still present; we did NOT delete the bad file.
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, bytes);
    }

    [Fact]
    public async Task Repair_DryRun_TouchesNothing()
    {
        var path = CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);

        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(_root, refetcher);
        var report = await repairer.RepairAsync(symbolFilter: null, dryRun: true, trustExisting: false);

        Assert.True(report.DryRun);
        Assert.Equal(1, report.ProblemsFound);
        Assert.Equal(0, report.Refetched);
        Assert.Equal(0, report.SidecarRegenerated);
        Assert.Equal(1, report.Pending);     // NotAttempted bucket
        Assert.Empty(refetcher.Calls);
        Assert.False(report.AllResolved);     // dry-runs never report success

        // File still has no sidecar — dry-run must not have written one.
        Assert.False(File.Exists(path + ".meta.json"));
    }

    [Fact]
    public async Task Repair_RespectsSymbolFilter()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, new byte[] { 5, 6, 7, 8 }, writeMeta: false);

        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(_root, refetcher);
        var report = await repairer.RepairAsync(new[] { "EURUSD" }, dryRun: false, trustExisting: false);

        Assert.Equal(1, report.FilesPlanned);
        Assert.Equal(1, report.ProblemsFound);
        Assert.Single(refetcher.Calls);
        Assert.Equal("EURUSD", refetcher.Calls[0].Symbol);
    }

    [Fact]
    public async Task Repair_TracksFilesProcessed()
    {
        for (var h = 0; h < 5; h++)
        {
            CreateTickFile("EURUSD", 2025, 0, 2, h, new byte[] { (byte)h, (byte)(h + 1) });
        }

        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(_root, refetcher);
        Assert.Equal(0, repairer.FilesProcessed);

        await repairer.RepairAsync(null, dryRun: false, trustExisting: false);
        Assert.Equal(5, repairer.FilesProcessed);
    }

    [Fact]
    public async Task Repair_PoolMissing_ReportsZeroFiles()
    {
        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(Path.Combine(_root, "nope"), refetcher);
        var report = await repairer.RepairAsync(null, dryRun: false, trustExisting: false);

        Assert.Equal(0, report.FilesPlanned);
        Assert.Equal(0, report.ProblemsFound);
        Assert.Empty(refetcher.Calls);
    }

    [Fact]
    public async Task Repair_RespectsCancellation()
    {
        for (var h = 0; h < 5; h++)
        {
            CreateTickFile("EURUSD", 2025, 0, 2, h, new byte[] { (byte)h }, writeMeta: false);
        }

        var refetcher = NewRefetcher();
        var repairer = new CacheRepairer(_root, refetcher);
        var plan = repairer.PlanFiles();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            repairer.RunPlanAsync(plan, dryRun: false, trustExisting: false, cts.Token));
    }

    [Fact]
    public void TryParseCacheFilePath_RoundTrip()
    {
        // Use Path.Combine so the test is platform-agnostic.
        var path = Path.Combine("D:", "MarketData", "EURUSD", "2025", "00", "02", "10h_ticks.bi5");
        var ok = CacheRepairer.TryParseCacheFilePath(path, out var symbol, out var year, out var month, out var day, out var fileName);
        Assert.True(ok);
        Assert.Equal("EURUSD", symbol);
        Assert.Equal(2025, year);
        Assert.Equal(0, month);
        Assert.Equal(2, day);
        Assert.Equal("10h_ticks.bi5", fileName);
    }

    [Fact]
    public void TryParseCacheFilePath_FailsOnNonNumericSegments()
    {
        var path = Path.Combine(_root, "EURUSD", "not-a-year", "00", "02", "10h_ticks.bi5");
        var ok = CacheRepairer.TryParseCacheFilePath(path, out _, out _, out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public async Task Render_DryRun_AnnouncesNoChanges()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);
        var report = await new CacheRepairer(_root, NewRefetcher()).RepairAsync(null, dryRun: true, trustExisting: false);
        var rendered = report.Render();
        Assert.Contains("DRY RUN", rendered);
        Assert.Contains("Re-run without --dry-run", rendered);
    }

    [Fact]
    public async Task Render_AllResolved_AnnouncesSuccess()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);
        var report = await new CacheRepairer(_root, NewRefetcher()).RepairAsync(null, dryRun: false, trustExisting: true);
        var rendered = report.Render();
        Assert.Contains("All problems resolved", rendered);
    }

    [Fact]
    public async Task Render_OutstandingProblems_PointsAtVerify()
    {
        var path = CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(path, new byte[] { 9, 9, 9, 9 });

        var refetcher = NewRefetcher();
        refetcher.CannedBytes = _ => null;
        var report = await new CacheRepairer(_root, refetcher).RepairAsync(null, dryRun: false, trustExisting: false);
        var rendered = report.Render();
        Assert.Contains("file(s) still bad", rendered);
        Assert.Contains("cache verify", rendered);
    }
}
