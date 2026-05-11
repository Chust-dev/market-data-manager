using HistoricalData.DataPool;
using HistoricalData.Manage;

namespace HistoricalData.Tests;

public sealed class CacheCleanerTests : IDisposable
{
    private readonly string _root;

    public CacheCleanerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CacheCleanerTests_" + Guid.NewGuid().ToString("N"));
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

    private string DayDir(string symbol, int year = 2025, int dukaMonth = 0, int day = 2)
    {
        var dir = Path.Combine(_root, symbol, year.ToString("0000"), dukaMonth.ToString("00"), day.ToString("00"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private string CreateBi5(string symbol, int hour, byte[] bytes, bool writeMeta = true, int year = 2025, int dukaMonth = 0, int day = 2)
    {
        var dir = DayDir(symbol, year, dukaMonth, day);
        var path = Path.Combine(dir, $"{hour:00}h_ticks.bi5");
        File.WriteAllBytes(path, bytes);
        if (writeMeta && bytes.Length > 0)
        {
            DataPoolFileMeta.Write(path);
        }
        return path;
    }

    private string CreateOrphanSidecar(string symbol, int hour)
    {
        var dir = DayDir(symbol);
        var dataPath = Path.Combine(dir, $"{hour:00}h_ticks.bi5");
        // Don't create the data file. Write a sidecar that points at the missing path.
        var metaPath = dataPath + ".meta.json";
        File.WriteAllText(metaPath, "{\"sha256\":\"deadbeef\",\"size\":0,\"downloadedUtc\":\"2025-01-01T00:00:00Z\"}");
        return metaPath;
    }

    private string CreateTmp(string symbol, string name)
    {
        var dir = DayDir(symbol);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 0x01, 0x02 });
        return path;
    }

    [Fact]
    public void Cleanup_DryRun_TouchesNothing()
    {
        var bad = CreateBi5("EURUSD", 10, Array.Empty<byte>(), writeMeta: false);
        var orphan = CreateOrphanSidecar("EURUSD", 11);
        var tmp = CreateTmp("EURUSD", "12h_ticks.bi5.tmp");

        var cleaner = new CacheCleaner(_root);
        var report = cleaner.Cleanup(symbolFilter: null, dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(3, report.Targets.Count);
        Assert.Equal(0, report.EmptyDirsRemoved);

        // Files must still exist after a dry run.
        Assert.True(File.Exists(bad));
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(tmp));
    }

    [Fact]
    public void Cleanup_Apply_DeletesZeroByteFiles()
    {
        var bad = CreateBi5("EURUSD", 10, Array.Empty<byte>(), writeMeta: false);
        var good = CreateBi5("EURUSD", 11, new byte[] { 1, 2, 3, 4 });

        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);

        Assert.False(File.Exists(bad));
        Assert.True(File.Exists(good));
        Assert.Equal(1, report.ZeroByteCount);
    }

    [Fact]
    public void Cleanup_Apply_AlsoDeletesAccompanyingSidecarOfZeroByteFile()
    {
        var bad = CreateBi5("EURUSD", 10, Array.Empty<byte>(), writeMeta: false);
        // Manually create a sidecar for the zero-byte file (could exist from a botched download).
        var meta = bad + ".meta.json";
        File.WriteAllText(meta, "{\"sha256\":\"x\",\"size\":0,\"downloadedUtc\":\"2025-01-01T00:00:00Z\"}");

        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);

        Assert.False(File.Exists(bad));
        Assert.False(File.Exists(meta));
        // Both reasons surface: ZeroByte and OrphanSidecar (the sidecar's data file was zero-byte and got removed).
        Assert.Equal(1, report.ZeroByteCount);
        Assert.Equal(1, report.OrphanSidecarCount);
    }

    [Fact]
    public void Cleanup_Apply_DeletesOrphanSidecars()
    {
        var orphan = CreateOrphanSidecar("EURUSD", 10);
        var good = CreateBi5("EURUSD", 11, new byte[] { 1, 2, 3, 4 });

        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(good));
        Assert.True(File.Exists(good + ".meta.json"));
        Assert.Equal(1, report.OrphanSidecarCount);
    }

    [Fact]
    public void Cleanup_Apply_DeletesTmpFiles()
    {
        var tmp1 = CreateTmp("EURUSD", "10h_ticks.bi5.tmp");
        var tmp2 = CreateTmp("EURUSD", "BID_candles_min_1.bi5.tmp");
        var good = CreateBi5("EURUSD", 11, new byte[] { 1, 2, 3, 4 });

        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);

        Assert.False(File.Exists(tmp1));
        Assert.False(File.Exists(tmp2));
        Assert.True(File.Exists(good));
        Assert.Equal(2, report.OrphanTmpCount);
    }

    [Fact]
    public void Cleanup_Apply_PreservesCompletelyHealthyFiles()
    {
        // No bad files at all.
        var p = CreateBi5("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);

        Assert.True(File.Exists(p));
        Assert.True(File.Exists(p + ".meta.json"));
        Assert.Empty(report.Targets);
    }

    [Fact]
    public void Cleanup_Apply_PrunesEmptyDayDirectory()
    {
        // Single zero-byte file in an otherwise-empty day folder.
        var path = CreateBi5("EURUSD", 10, Array.Empty<byte>(), writeMeta: false);
        var dayDir = Path.GetDirectoryName(path)!;

        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(dayDir));
        Assert.True(report.EmptyDirsRemoved >= 1);
    }

    [Fact]
    public void Cleanup_Apply_LeavesSymbolRootEvenIfEmpty()
    {
        // Single zero-byte file under a symbol — after cleanup the day/month/year
        // folders should be pruned, but the symbol root itself should remain
        // (deleting symbol directories is too aggressive for cleanup).
        var path = CreateBi5("EURUSD", 10, Array.Empty<byte>(), writeMeta: false);

        new CacheCleaner(_root).Cleanup(null, dryRun: false);

        Assert.True(Directory.Exists(Path.Combine(_root, "EURUSD")));
    }

    [Fact]
    public void Cleanup_RespectsSymbolFilter()
    {
        var eu = CreateBi5("EURUSD", 10, Array.Empty<byte>(), writeMeta: false);
        var gbp = CreateBi5("GBPUSD", 10, Array.Empty<byte>(), writeMeta: false);

        new CacheCleaner(_root).Cleanup(new[] { "EURUSD" }, dryRun: false);

        Assert.False(File.Exists(eu));
        Assert.True(File.Exists(gbp)); // GBPUSD untouched
    }

    [Fact]
    public void Cleanup_TracksFilesProcessed()
    {
        for (var h = 0; h < 5; h++)
        {
            CreateBi5("EURUSD", h, Array.Empty<byte>(), writeMeta: false);
        }

        var cleaner = new CacheCleaner(_root);
        Assert.Equal(0, cleaner.FilesProcessed);

        cleaner.Cleanup(null, dryRun: false);
        Assert.Equal(5, cleaner.FilesProcessed);
    }

    [Fact]
    public void Cleanup_PoolMissing_ReportsZero()
    {
        var report = new CacheCleaner(Path.Combine(_root, "nope")).Cleanup(null, dryRun: false);
        Assert.Equal(0, report.FilesPlanned);
        Assert.Empty(report.Targets);
    }

    [Fact]
    public void RunPlan_RespectsCancellation()
    {
        for (var h = 0; h < 5; h++)
        {
            CreateTmp("EURUSD", $"{h:00}h.bi5.tmp");
        }

        var cleaner = new CacheCleaner(_root);
        var plan = cleaner.PlanCleanup();
        Assert.Equal(5, plan.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => cleaner.RunPlan(plan, dryRun: false, cts.Token));
    }

    [Fact]
    public void Render_DryRun_AnnouncesNoChanges()
    {
        CreateTmp("EURUSD", "10h.bi5.tmp");
        var report = new CacheCleaner(_root).Cleanup(null, dryRun: true);
        var rendered = report.Render();
        Assert.Contains("DRY RUN", rendered);
        Assert.Contains("Re-run without --dry-run", rendered);
    }

    [Fact]
    public void Render_Apply_AnnouncesDeletion()
    {
        CreateTmp("EURUSD", "10h.bi5.tmp");
        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);
        var rendered = report.Render();
        Assert.Contains("Cleanup applied", rendered);
        Assert.Contains("files deleted", rendered);
    }

    [Fact]
    public void Render_NothingToRemove()
    {
        CreateBi5("EURUSD", 10, new byte[] { 1, 2, 3, 4 });
        var report = new CacheCleaner(_root).Cleanup(null, dryRun: false);
        var rendered = report.Render();
        Assert.Contains("Nothing to remove", rendered);
    }

    // ---------- PurgeSymbol (cache cleanup --purge) ----------

    [Fact]
    public void Purge_DryRun_TouchesNothing()
    {
        var bi5 = CreateBi5("BTCUSD", 10, new byte[] { 1, 2, 3, 4 });
        var bi5b = CreateBi5("BTCUSD", 11, new byte[] { 5, 6, 7, 8 });
        var symbolDir = Path.Combine(_root, "BTCUSD");

        var report = new CacheCleaner(_root).PurgeSymbol("BTCUSD", dryRun: true);

        Assert.True(report.DryRun);
        Assert.Equal(2 + 2, report.FilesRemoved); // 2 .bi5 + 2 .meta.json
        Assert.True(report.BytesRemoved > 0);

        // Files still on disk after dry-run.
        Assert.True(File.Exists(bi5));
        Assert.True(File.Exists(bi5b));
        Assert.True(Directory.Exists(symbolDir));
    }

    [Fact]
    public void Purge_Apply_WipesSymbolDirectory()
    {
        CreateBi5("BTCUSD", 10, new byte[] { 1, 2, 3, 4 });
        CreateBi5("BTCUSD", 11, new byte[] { 5, 6, 7, 8 });
        // Also create another symbol that should be left alone.
        var keep = CreateBi5("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        var report = new CacheCleaner(_root).PurgeSymbol("BTCUSD", dryRun: false);

        Assert.False(report.DryRun);
        Assert.Equal(4, report.FilesRemoved); // 2 .bi5 + 2 .meta.json
        Assert.True(report.BytesRemoved > 0);
        Assert.False(report.AnyFailures);

        // Symbol directory is gone entirely.
        Assert.False(Directory.Exists(Path.Combine(_root, "BTCUSD")));
        // Unrelated symbol untouched.
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void Purge_NonexistentSymbol_ReturnsZeroTotals()
    {
        var report = new CacheCleaner(_root).PurgeSymbol("GHOSTSYM", dryRun: false);

        Assert.Equal(0, report.FilesRemoved);
        Assert.Equal(0, report.BytesRemoved);
        Assert.False(report.AnyFailures);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Purge_BlankSymbol_NoOp(string symbol)
    {
        CreateBi5("EURUSD", 10, new byte[] { 1, 2, 3, 4 });
        var report = new CacheCleaner(_root).PurgeSymbol(symbol, dryRun: false);

        Assert.Equal(0, report.FilesRemoved);
        // The unrelated symbol survives.
        Assert.True(Directory.Exists(Path.Combine(_root, "EURUSD")));
    }

    [Fact]
    public void Purge_RejectsPathTraversal()
    {
        CreateBi5("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        // A maliciously-named symbol with a path separator must NOT reach
        // outside the pool root. Path.GetFileName strips the prefix; if the
        // result differs from the input, we treat it as invalid.
        var bad = Path.Combine("..", "EURUSD");
        var report = new CacheCleaner(_root).PurgeSymbol(bad, dryRun: false);

        Assert.Equal(0, report.FilesRemoved);
        Assert.True(report.AnyFailures);
        // Unrelated data untouched.
        Assert.True(Directory.Exists(Path.Combine(_root, "EURUSD")));
    }

    [Fact]
    public void Purge_Render_DryRunAnnouncesNoChanges()
    {
        CreateBi5("BTCUSD", 10, new byte[] { 1, 2, 3, 4 });
        var report = new CacheCleaner(_root).PurgeSymbol("BTCUSD", dryRun: true);
        var rendered = report.Render();

        Assert.Contains("DRY RUN", rendered);
        Assert.Contains("Re-run without --dry-run", rendered);
    }

    [Fact]
    public void Purge_Render_ApplyAnnouncesSymbol()
    {
        CreateBi5("BTCUSD", 10, new byte[] { 1, 2, 3, 4 });
        var report = new CacheCleaner(_root).PurgeSymbol("BTCUSD", dryRun: false);
        var rendered = report.Render();

        Assert.Contains("Purged BTCUSD", rendered);
        Assert.Contains("Files removed", rendered);
    }

    [Fact]
    public void Purge_Render_NonexistentSymbolFriendlyMessage()
    {
        var report = new CacheCleaner(_root).PurgeSymbol("GHOSTSYM", dryRun: false);
        var rendered = report.Render();

        Assert.Contains("Nothing to purge", rendered);
    }
}
