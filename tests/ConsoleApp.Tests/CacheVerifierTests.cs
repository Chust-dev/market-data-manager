using HistoricalData.Manage;
using HistoricalData.DataPool;

namespace HistoricalData.Tests;

public sealed class CacheVerifierTests : IDisposable
{
    private readonly string _root;

    public CacheVerifierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CacheVerifierTests_" + Guid.NewGuid().ToString("N"));
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
    /// Creates a .bi5 file at pool/symbol/yyyy/mm/dd/HHh_ticks.bi5 with random
    /// bytes, and (unless <paramref name="writeMeta"/> is false) writes the
    /// matching .meta.json sidecar via DataPoolFileMeta.
    /// </summary>
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

    [Fact]
    public void Verify_ReturnsAllClean_WhenAllFilesValid()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        CreateTickFile("EURUSD", 2025, 0, 2, 11, new byte[] { 5, 6, 7, 8 });

        var verifier = new CacheVerifier(_root);
        var report = verifier.Verify();

        Assert.True(report.PoolExists);
        Assert.Equal(2, report.FilesPlanned);
        Assert.Equal(2, report.TotalOk);
        Assert.Equal(0, report.TotalProblems);
        Assert.True(report.AllClean);
    }

    [Fact]
    public void Verify_DetectsMissingMetadata()
    {
        // First file has metadata, second doesn't.
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        CreateTickFile("EURUSD", 2025, 0, 2, 11, new byte[] { 5, 6, 7, 8 }, writeMeta: false);

        var verifier = new CacheVerifier(_root);
        var report = verifier.Verify();

        Assert.Equal(1, report.TotalOk);
        Assert.Equal(1, report.TotalNoMetadata);
        Assert.False(report.AllClean);
        Assert.Single(report.Mismatches);
        Assert.Equal(VerifyOutcome.NoMetadata, report.Mismatches[0].Outcome);
    }

    [Fact]
    public void Verify_DetectsSizeMismatch()
    {
        // Write meta against original bytes, then grow the file so size disagrees.
        var path = CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });

        var verifier = new CacheVerifier(_root);
        var report = verifier.Verify();

        Assert.Equal(0, report.TotalOk);
        Assert.Equal(1, report.TotalSizeMismatch);
        Assert.False(report.AllClean);
        Assert.Single(report.Mismatches);
        Assert.Equal(VerifyOutcome.SizeMismatch, report.Mismatches[0].Outcome);
    }

    [Fact]
    public void Verify_DetectsHashMismatch()
    {
        // Write meta against original bytes, then overwrite with different bytes
        // of the SAME length so the size check passes but SHA-256 fails.
        var path = CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(path, new byte[] { 9, 9, 9, 9 });

        var verifier = new CacheVerifier(_root);
        var report = verifier.Verify();

        Assert.Equal(0, report.TotalOk);
        Assert.Equal(0, report.TotalSizeMismatch);
        Assert.Equal(1, report.TotalHashMismatch);
        Assert.False(report.AllClean);
        Assert.Equal(VerifyOutcome.HashMismatch, report.Mismatches[0].Outcome);
    }

    [Fact]
    public void Verify_RespectsSymbolFilter()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        CreateTickFile("GBPUSD", 2025, 0, 2, 10, new byte[] { 5, 6, 7, 8 });
        CreateTickFile("XAUUSD", 2025, 0, 2, 10, new byte[] { 9, 10, 11, 12 });

        var verifier = new CacheVerifier(_root);
        var report = verifier.Verify(new[] { "EURUSD", "XAUUSD" });

        Assert.Equal(2, report.FilesPlanned);
        Assert.Equal(2, report.Symbols.Count);
        Assert.DoesNotContain(report.Symbols, s => s.Symbol == "GBPUSD");
    }

    [Fact]
    public void Verify_HandlesMissingPool()
    {
        var verifier = new CacheVerifier(Path.Combine(_root, "nope"));
        var report = verifier.Verify();

        Assert.False(report.PoolExists);
        Assert.False(report.AllClean);
        Assert.Equal(0, report.FilesPlanned);
        Assert.Empty(report.Symbols);
    }

    [Fact]
    public void Verify_AllCleanIsFalse_WhenPoolIsEmpty()
    {
        // Pool exists but has no .bi5 files: AllClean must be false because
        // "verifying nothing" is not the same as "everything is fine."
        var verifier = new CacheVerifier(_root);
        var report = verifier.Verify();

        Assert.True(report.PoolExists);
        Assert.Equal(0, report.FilesPlanned);
        Assert.False(report.AllClean);
    }

    [Fact]
    public void Verify_TracksFilesProcessed()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        CreateTickFile("EURUSD", 2025, 0, 2, 11, new byte[] { 5, 6, 7, 8 });
        CreateTickFile("EURUSD", 2025, 0, 2, 12, new byte[] { 9, 10, 11, 12 });

        var verifier = new CacheVerifier(_root);
        Assert.Equal(0, verifier.FilesProcessed);

        verifier.Verify();
        Assert.Equal(3, verifier.FilesProcessed);
    }

    [Fact]
    public void RunPlan_RespectsCancellation()
    {
        for (var h = 0; h < 5; h++)
        {
            CreateTickFile("EURUSD", 2025, 0, 2, h, new byte[] { (byte)h });
        }

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        Assert.Equal(5, plan.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => verifier.RunPlan(plan, cts.Token));
    }

    [Fact]
    public void Render_AllClean_ProducesPositiveMessage()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 });
        var report = new CacheVerifier(_root).Verify();
        var rendered = report.Render();
        Assert.Contains("All cached files verified clean", rendered);
    }

    [Fact]
    public void Render_WithProblems_ListsRemediation()
    {
        CreateTickFile("EURUSD", 2025, 0, 2, 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);
        var report = new CacheVerifier(_root).Verify();
        var rendered = report.Render();
        Assert.Contains("local problem(s) found", rendered);
        // Now points users at the dedicated repair / cleanup tools.
        Assert.Contains("cache repair", rendered);
    }

    // ---------- Date range filter (cache verify --start / --end) ----------

    [Fact]
    public void PlanFiles_FromUtc_ExcludesEarlierDays()
    {
        // Three files: 2024-01, 2024-06, 2025-03 (all dukaMonth values).
        CreateTickFile("EURUSD", 2024, 0, 15, 10, new byte[] { 1 });   // 2024-01-15
        CreateTickFile("EURUSD", 2024, 5, 15, 10, new byte[] { 2 });   // 2024-06-15
        CreateTickFile("EURUSD", 2025, 2, 15, 10, new byte[] { 3 });   // 2025-03-15

        var verifier = new CacheVerifier(_root);
        // From 2025-01-01: only the 2025-03 file should remain.
        var plan = verifier.PlanFiles(
            symbolFilter: null,
            fromUtc: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            toUtc: null);

        Assert.Single(plan);
        Assert.Contains("2025", plan[0].Path);
    }

    [Fact]
    public void PlanFiles_ToUtc_ExcludesLaterDays()
    {
        CreateTickFile("EURUSD", 2024, 0, 15, 10, new byte[] { 1 });   // 2024-01-15
        CreateTickFile("EURUSD", 2024, 5, 15, 10, new byte[] { 2 });   // 2024-06-15
        CreateTickFile("EURUSD", 2025, 2, 15, 10, new byte[] { 3 });   // 2025-03-15

        var verifier = new CacheVerifier(_root);
        // Up to 2024-12-31: the first two files only.
        var plan = verifier.PlanFiles(
            symbolFilter: null,
            fromUtc: null,
            toUtc: new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, plan.Count);
        Assert.All(plan, t => Assert.Contains("2024", t.Path));
    }

    [Fact]
    public void PlanFiles_BothBounds_NarrowsToRange()
    {
        CreateTickFile("EURUSD", 2024, 0, 15, 10, new byte[] { 1 });   // 2024-01-15
        CreateTickFile("EURUSD", 2024, 5, 15, 10, new byte[] { 2 });   // 2024-06-15
        CreateTickFile("EURUSD", 2025, 2, 15, 10, new byte[] { 3 });   // 2025-03-15

        var verifier = new CacheVerifier(_root);
        // Between 2024-03-01 and 2024-09-30: only the 2024-06 file.
        var plan = verifier.PlanFiles(
            symbolFilter: null,
            fromUtc: new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero),
            toUtc:   new DateTimeOffset(2024, 9, 30, 0, 0, 0, TimeSpan.Zero));

        Assert.Single(plan);
        // 2024-06 cached under dukaMonth folder "05".
        Assert.Contains(Path.Combine("2024", "05"), plan[0].Path);
    }

    [Fact]
    public void PlanFiles_EmptyRange_ReturnsNothing()
    {
        CreateTickFile("EURUSD", 2024, 0, 15, 10, new byte[] { 1 });

        var verifier = new CacheVerifier(_root);
        // Range entirely after the only file.
        var plan = verifier.PlanFiles(
            symbolFilter: null,
            fromUtc: new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            toUtc:   new DateTimeOffset(2030, 12, 31, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(plan);
    }

    [Fact]
    public void PlanFiles_NullBounds_ReturnsEverything()
    {
        CreateTickFile("EURUSD", 2024, 0, 15, 10, new byte[] { 1 });
        CreateTickFile("EURUSD", 2025, 2, 15, 10, new byte[] { 2 });

        var verifier = new CacheVerifier(_root);
        // null/null == no date filtering.
        var plan = verifier.PlanFiles(symbolFilter: null, fromUtc: null, toUtc: null);

        Assert.Equal(2, plan.Count);
    }
}
