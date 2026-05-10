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
    /// results keyed on file name. Lets each test arrange exactly the
    /// remote drift scenario it cares about.
    /// </summary>
    private sealed class FakeRemoteFileProbe : IRemoteFileProbe
    {
        public List<(string Symbol, int Year, int Month, int Day, string FileName, bool FetchBody)> Calls { get; } = new();
        public Func<string, RemoteProbeResult> ResultFor { get; set; } =
            _ => new RemoteProbeResult(RemoteProbeStatus.Ok, 4, null);

        public Task<RemoteProbeResult> ProbeAsync(string symbol, int year, int dukaMonth, int day, string fileName, bool fetchBody, CancellationToken cancellationToken)
        {
            Calls.Add((symbol, year, dukaMonth, day, fileName, fetchBody));
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
        Assert.True(probe.Calls[0].FetchBody);
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
}
