using System.Security.Cryptography;
using HistoricalData.DataPool;
using HistoricalData.Manage;

namespace HistoricalData.Tests;

public sealed class CacheVerifierRemoteTests : IDisposable
{
    private readonly string _root;

    public CacheVerifierRemoteTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "CacheVerifierRemoteTests_" + Guid.NewGuid().ToString("N"));
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

    private string CreateTickFile(string symbol, int hour, byte[] bytes, bool writeMeta = true, int year = 2025, int dukaMonth = 0, int day = 2)
    {
        var dir = Path.Combine(_root, symbol, year.ToString("0000"), dukaMonth.ToString("00"), day.ToString("00"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{hour:00}h_ticks.bi5");
        File.WriteAllBytes(path, bytes);
        if (writeMeta && bytes.Length > 0)
        {
            DataPoolFileMeta.Write(path);
        }
        return path;
    }

    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    /// <summary>
    /// FakeRemoteFileProbe — records every probe call and returns canned
    /// results keyed on file name. Thread-safe so it can stand in for the
    /// real probe under parallel verification. Lets each test arrange
    /// exactly the remote drift scenario it cares about.
    /// </summary>
    private sealed class FakeRemoteFileProbe : IRemoteFileProbe
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(string Symbol, int Year, int Month, int Day, string FileName, bool FetchBody)> _calls = new();
        public IReadOnlyCollection<(string Symbol, int Year, int Month, int Day, string FileName, bool FetchBody)> Calls => _calls;
        public Func<string, RemoteProbeResult> ResultFor { get; set; } =
            _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 4, null);

        public Task<RemoteProbeResult> ProbeAsync(string symbol, int year, int dukaMonth, int day, string fileName, bool fetchBody, CancellationToken cancellationToken)
        {
            _calls.Enqueue((symbol, year, dukaMonth, day, fileName, fetchBody));
            return Task.FromResult(ResultFor(fileName));
        }
    }

    [Fact]
    public async Task Remote_AllSizesMatch_ReportsAllClean()
    {
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });
        CreateTickFile("EURUSD", 11, new byte[] { 5, 6, 7, 8 });

        var probe = new FakeRemoteFileProbe
        {
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 4, null)
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);

        Assert.True(report.RemoteCheckPerformed);
        Assert.False(report.RemoteByteExact);
        Assert.Equal(2, report.TotalRemoteOk);
        Assert.Equal(0, report.TotalRemoteProblems);
        Assert.True(report.AllClean);
    }

    [Fact]
    public async Task Remote_SizeDiffers_FlagsRemoteSizeMismatch()
    {
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        var probe = new FakeRemoteFileProbe
        {
            // Local file is 4 bytes; pretend Dukascopy reports 5 bytes.
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 5, null)
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);

        Assert.Equal(0, report.TotalRemoteOk);
        Assert.Equal(1, report.TotalRemoteSizeMismatch);
        Assert.False(report.AllClean);
        Assert.Single(report.RemoteMismatches);
        Assert.Equal(RemoteVerifyOutcome.RemoteSizeMismatch, report.RemoteMismatches[0].Outcome);
        Assert.Equal(4, report.RemoteMismatches[0].LocalSize);
        Assert.Equal(5, report.RemoteMismatches[0].RemoteSize);
    }

    [Fact]
    public async Task Remote_NotFound_FlagsRemoteUnreachable()
    {
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        var probe = new FakeRemoteFileProbe
        {
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.NotFound, null, null)
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);

        Assert.Equal(1, report.TotalRemoteUnreachable);
        Assert.False(report.AllClean);
    }

    [Fact]
    public async Task Remote_ByteExact_HashesAndDetectsContentChange()
    {
        // Local bytes have known sidecar SHA-256.
        var bytes = new byte[] { 1, 2, 3, 4 };
        CreateTickFile("EURUSD", 10, bytes);
        var localHash = Sha256Hex(bytes);

        // Probe returns SAME size but DIFFERENT hash — content changed underneath us.
        var probe = new FakeRemoteFileProbe
        {
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 4, "DEADBEEFCAFEBABE")
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: true);

        Assert.True(report.RemoteByteExact);
        Assert.Equal(0, report.TotalRemoteOk);
        Assert.Equal(1, report.TotalRemoteHashMismatch);
        Assert.False(report.AllClean);

        // The fake should have been asked to fetch the body (byteExact=true).
        Assert.Single(probe.Calls);
        Assert.True(probe.Calls.First().FetchBody);
        // Sanity: the local bytes really do hash to localHash (so the comparison is meaningful).
        Assert.NotEqual("DEADBEEFCAFEBABE", localHash);
    }

    [Fact]
    public async Task Remote_ByteExact_MatchingHashIsClean()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        CreateTickFile("EURUSD", 10, bytes);
        var localHash = Sha256Hex(bytes);

        var probe = new FakeRemoteFileProbe
        {
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.Ok, bytes.Length, localHash)
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: true);

        Assert.Equal(1, report.TotalRemoteOk);
        Assert.True(report.AllClean);
    }

    [Fact]
    public async Task Remote_LocallyBadFiles_AreNotProbed()
    {
        // Two files; one has a tampered hash (locally bad), one is locally clean.
        var bad = CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(bad, new byte[] { 9, 9, 9, 9 }); // hash now disagrees with sidecar
        CreateTickFile("EURUSD", 11, new byte[] { 5, 6, 7, 8 });

        var probe = new FakeRemoteFileProbe
        {
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 4, null)
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);

        // Only the locally-clean file got probed. The locally-bad one was skipped.
        Assert.Single(probe.Calls);
        Assert.Equal(1, report.TotalHashMismatch);
        Assert.Equal(1, report.TotalRemoteOk);
        Assert.False(report.AllClean); // local hash mismatch still counts
    }

    [Fact]
    public async Task Remote_NoMetadataFiles_AreNotProbed()
    {
        // NoMetadata files (no sidecar) are locally non-Ok — also skipped.
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 }, writeMeta: false);

        var probe = new FakeRemoteFileProbe();
        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);

        Assert.Empty(probe.Calls);
        Assert.Equal(1, report.TotalNoMetadata);
        Assert.Equal(0, report.TotalRemoteChecked);
    }

    [Fact]
    public async Task Remote_MixedOutcomes_AllResponsesRecorded()
    {
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });        // → RemoteOk
        CreateTickFile("EURUSD", 11, new byte[] { 5, 6, 7, 8 });        // → RemoteSizeMismatch
        CreateTickFile("EURUSD", 12, new byte[] { 9, 10, 11, 12 });     // → RemoteUnreachable

        var probe = new FakeRemoteFileProbe
        {
            ResultFor = name => name switch
            {
                "10h_ticks.bi5" => new RemoteProbeResult(RemoteProbeStatus.Ok, 4, null),
                "11h_ticks.bi5" => new RemoteProbeResult(RemoteProbeStatus.Ok, 99, null),
                "12h_ticks.bi5" => new RemoteProbeResult(RemoteProbeStatus.Unreachable, null, null),
                _ => throw new InvalidOperationException($"Unexpected {name}")
            }
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);

        Assert.Equal(1, report.TotalRemoteOk);
        Assert.Equal(1, report.TotalRemoteSizeMismatch);
        Assert.Equal(1, report.TotalRemoteUnreachable);
        Assert.Equal(2, report.TotalRemoteProblems);
    }

    [Fact]
    public async Task Remote_NullProbe_Throws()
    {
        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            verifier.RunPlanWithRemoteAsync(plan, remoteProbe: null!, byteExact: false));
    }

    [Fact]
    public async Task Remote_RespectsCancellation()
    {
        for (var h = 0; h < 5; h++)
        {
            CreateTickFile("EURUSD", h, new byte[] { (byte)h, (byte)(h + 1) });
        }

        var probe = new FakeRemoteFileProbe();
        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false, cts.Token));
    }

    [Fact]
    public async Task Render_RemoteAllClean_AnnouncesBoth()
    {
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        var probe = new FakeRemoteFileProbe
        {
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 4, null)
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);
        var rendered = report.Render();

        Assert.Contains("Remote drift check", rendered);
        Assert.Contains("size-only", rendered);
        Assert.Contains("Dukascopy", rendered);
    }

    [Fact]
    public async Task Render_RemoteSizeDrift_PointsAtRepair()
    {
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        var probe = new FakeRemoteFileProbe
        {
            ResultFor = _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 99, null)
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);
        var rendered = report.Render();

        Assert.Contains("remote drift", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cache repair", rendered);
    }

    [Fact]
    public async Task Remote_ParallelProbing_PreservesCorrectness()
    {
        // 50 locally-clean files across two symbols. With parallelism=8 the
        // fake probe will be called concurrently; assertions check that the
        // aggregated report agrees with the per-file outcomes regardless.
        const int FilesPerSymbol = 25;
        for (var h = 0; h < FilesPerSymbol; h++)
        {
            CreateTickFile("EURUSD", h, new byte[] { (byte)h, (byte)(h + 1), 3, 4 });
            CreateTickFile("GBPUSD", h, new byte[] { (byte)h, (byte)(h + 1), 5, 6 });
        }

        // Half the EURUSD files have size drift; everything else is clean.
        var probe = new FakeRemoteFileProbe
        {
            ResultFor = name =>
            {
                // Encode "is this a drifty file" in the hour digits: hours 0-11 = drift, 12-24 = clean
                var hour = int.Parse(name[..2]);
                return hour < 12
                    ? new RemoteProbeResult(RemoteProbeStatus.Ok, 99, null)
                    : new RemoteProbeResult(RemoteProbeStatus.Ok, 4, null);
            }
        };

        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false, parallelism: 8);

        // 50 local-Ok files, each probed exactly once.
        Assert.Equal(50, probe.Calls.Count);
        Assert.Equal(50, report.TotalRemoteChecked);
        Assert.Equal(8, report.RemoteParallelism);

        // 24 drift (hours 0-11 across both symbols), 26 clean (hours 12-24 across both symbols).
        // EURUSD and GBPUSD each have 12 files in hours 0-11 → 24 total drift.
        Assert.Equal(24, report.TotalRemoteSizeMismatch);
        Assert.Equal(26, report.TotalRemoteOk);
        Assert.False(report.AllClean);

        // Counter must reach the total (no double-count, no skip).
        Assert.Equal(plan.Count, verifier.FilesProcessed);
    }

    [Fact]
    public async Task Remote_ParallelDefaultsToConst()
    {
        CreateTickFile("EURUSD", 10, new byte[] { 1, 2, 3, 4 });

        var probe = new FakeRemoteFileProbe();
        var verifier = new CacheVerifier(_root);
        var plan = verifier.PlanFiles();
        // Overload without explicit parallelism falls through to DefaultRemoteParallelism.
        var report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: false);

        Assert.Equal(CacheVerifier.DefaultRemoteParallelism, report.RemoteParallelism);
        Assert.Equal(8, report.RemoteParallelism); // sanity: default is 8
    }
}
