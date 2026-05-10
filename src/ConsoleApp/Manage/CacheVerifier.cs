using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HistoricalData.DataPool;

namespace HistoricalData.Manage;

/// <summary>
/// Offline checksum verification of cached .bi5 files. For each file in
/// the pool, reads the sidecar .meta.json, recomputes the SHA-256 of the
/// file, and compares against the recorded hash + size. Reports per-symbol
/// counts of OK / no-metadata / size-mismatch / hash-mismatch / I/O-error.
///
/// No network. Two-phase API so callers can attach a progress bar:
/// <see cref="PlanFiles"/> enumerates targets (fast, just directory listing);
/// <see cref="RunPlan"/> does the SHA-256 work (slow, CPU-bound on multi-GB
/// pools). Tests that don't care about progress can call <see cref="Verify"/>
/// for a one-shot run.
/// </summary>
public sealed class CacheVerifier
{
    private const string Bi5Suffix = ".bi5";

    private readonly string _poolPath;
    private long _filesProcessed;

    public CacheVerifier(string poolPath)
    {
        _poolPath = poolPath ?? throw new ArgumentNullException(nameof(poolPath));
    }

    /// <summary>
    /// Number of files whose verification has completed (success or failure).
    /// Atomically incremented as <see cref="RunPlan"/> progresses; safe to poll
    /// from another thread to drive a progress bar.
    /// </summary>
    public long FilesProcessed => Interlocked.Read(ref _filesProcessed);

    /// <summary>
    /// One-shot convenience: enumerate, then verify. No progress reporting —
    /// callers that want a progress bar should use <see cref="PlanFiles"/> +
    /// <see cref="RunPlan"/> separately so the bar can know the total upfront.
    /// </summary>
    public CacheVerifyReport Verify(IReadOnlyCollection<string>? symbolFilter = null, CancellationToken cancellationToken = default)
    {
        var plan = PlanFiles(symbolFilter);
        return RunPlan(plan, cancellationToken);
    }

    /// <summary>
    /// Pass 1: walk the pool and collect all .bi5 files matching the filter.
    /// Tagged with the symbol so the report can group results.
    /// </summary>
    public IReadOnlyList<VerifyTarget> PlanFiles(IReadOnlyCollection<string>? symbolFilter = null)
    {
        var plan = new List<VerifyTarget>();
        if (!Directory.Exists(_poolPath))
        {
            return plan;
        }

        var filter = symbolFilter is null
            ? null
            : new HashSet<string>(symbolFilter, StringComparer.OrdinalIgnoreCase);

        foreach (var symbolDir in Directory.EnumerateDirectories(_poolPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var symbol = Path.GetFileName(symbolDir);
            if (filter is not null && !filter.Contains(symbol))
            {
                continue;
            }

            foreach (var yearDir in Directory.EnumerateDirectories(symbolDir))
            {
                if (!int.TryParse(Path.GetFileName(yearDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                foreach (var monthDir in Directory.EnumerateDirectories(yearDir))
                {
                    if (!int.TryParse(Path.GetFileName(monthDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        continue;
                    }

                    foreach (var dayDir in Directory.EnumerateDirectories(monthDir))
                    {
                        if (!int.TryParse(Path.GetFileName(dayDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        {
                            continue;
                        }

                        foreach (var file in Directory.EnumerateFiles(dayDir, "*" + Bi5Suffix))
                        {
                            plan.Add(new VerifyTarget(symbol, file));
                        }
                    }
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Pass 2 (local only): verify each file in the plan against its sidecar.
    /// Resets and increments <see cref="FilesProcessed"/> as it goes. Throws
    /// <see cref="OperationCanceledException"/> on cancellation.
    /// </summary>
    public CacheVerifyReport RunPlan(IReadOnlyList<VerifyTarget> plan, CancellationToken cancellationToken = default)
    {
        return RunPlanInternal(plan, remoteProbe: null, byteExact: false, cancellationToken)
            .GetAwaiter()
            .GetResult();
    }

    /// <summary>
    /// Pass 2 (local + remote): same as <see cref="RunPlan"/> but additionally
    /// probes Dukascopy for each locally-Ok file to detect drift between the
    /// cache and the source. Files that fail the local check are NOT probed
    /// remotely (they're already known-bad — no point burning network on
    /// them). Each locally-Ok file gets a <see cref="RemoteVerifyOutcome"/>
    /// recorded alongside its local outcome.
    ///
    /// <paramref name="byteExact"/> = true downloads the body and SHA-256s it
    /// for byte-exact compare; false fetches only Content-Length for a fast
    /// size-only check.
    /// </summary>
    public Task<CacheVerifyReport> RunPlanWithRemoteAsync(
        IReadOnlyList<VerifyTarget> plan,
        IRemoteFileProbe remoteProbe,
        bool byteExact,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteProbe);
        return RunPlanInternal(plan, remoteProbe, byteExact, cancellationToken);
    }

    private async Task<CacheVerifyReport> RunPlanInternal(
        IReadOnlyList<VerifyTarget> plan,
        IRemoteFileProbe? remoteProbe,
        bool byteExact,
        CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _filesProcessed, 0);

        var report = new CacheVerifyReport
        {
            PoolPath = _poolPath,
            PoolExists = Directory.Exists(_poolPath),
            FilesPlanned = plan.Count,
            RemoteCheckPerformed = remoteProbe is not null,
            RemoteByteExact = remoteProbe is not null && byteExact
        };

        var bySymbol = new Dictionary<string, SymbolVerifyReport>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!bySymbol.TryGetValue(target.Symbol, out var symbolReport))
            {
                symbolReport = new SymbolVerifyReport { Symbol = target.Symbol };
                bySymbol[target.Symbol] = symbolReport;
            }

            var localOutcome = Inspect(target.Path);
            symbolReport.Increment(localOutcome);

            if (localOutcome != VerifyOutcome.Ok && report.Mismatches.Count < CacheVerifyReport.MaxMismatchesShown)
            {
                report.Mismatches.Add(new VerifyMismatch(target.Symbol, target.Path, localOutcome));
            }

            // Only run remote check on locally-clean files. Probing a known-bad
            // file wastes network without telling us anything new.
            if (remoteProbe is not null && localOutcome == VerifyOutcome.Ok)
            {
                var (remoteOutcome, localSize, remoteSize) = await CheckRemoteAsync(target, remoteProbe, byteExact, cancellationToken);
                symbolReport.IncrementRemote(remoteOutcome);
                if (remoteOutcome != RemoteVerifyOutcome.RemoteOk
                    && report.RemoteMismatches.Count < CacheVerifyReport.MaxMismatchesShown)
                {
                    report.RemoteMismatches.Add(new RemoteVerifyMismatch(target.Symbol, target.Path, remoteOutcome, localSize, remoteSize));
                }
            }

            Interlocked.Increment(ref _filesProcessed);
        }

        report.Symbols.AddRange(bySymbol.Values.OrderBy(s => s.Symbol, StringComparer.OrdinalIgnoreCase));
        return report;
    }

    private static async Task<(RemoteVerifyOutcome Outcome, long? LocalSize, long? RemoteSize)> CheckRemoteAsync(
        VerifyTarget target,
        IRemoteFileProbe probe,
        bool byteExact,
        CancellationToken cancellationToken)
    {
        if (!CacheRepairer.TryParseCacheFilePath(target.Path, out var symbol, out var year, out var month, out var day, out var fileName))
        {
            // Path unparseable — treat as unreachable so it surfaces in the report.
            return (RemoteVerifyOutcome.RemoteUnreachable, null, null);
        }

        long? localSize = null;
        try
        {
            localSize = new FileInfo(target.Path).Length;
        }
        catch
        {
            return (RemoteVerifyOutcome.RemoteUnreachable, null, null);
        }

        var result = await probe.ProbeAsync(symbol, year, month, day, fileName, byteExact, cancellationToken);

        if (result.Status == RemoteProbeStatus.NotFound || result.Status == RemoteProbeStatus.Unreachable)
        {
            return (RemoteVerifyOutcome.RemoteUnreachable, localSize, null);
        }

        // Status == Ok
        if (result.ContentLength is long remoteLen && remoteLen != localSize)
        {
            return (RemoteVerifyOutcome.RemoteSizeMismatch, localSize, remoteLen);
        }

        if (byteExact && result.Sha256 is { } remoteHash)
        {
            // Read the local file's hash from its sidecar — it was just verified
            // matches local bytes, so this is equivalent to hashing the file again
            // but cheaper.
            if (DataPoolFileMeta.TryRead(target.Path, out var meta)
                && !string.IsNullOrEmpty(meta.Sha256)
                && !meta.Sha256.Equals(remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                return (RemoteVerifyOutcome.RemoteHashMismatch, localSize, result.ContentLength);
            }
        }

        return (RemoteVerifyOutcome.RemoteOk, localSize, result.ContentLength);
    }

    /// <summary>
    /// Inspects a single .bi5 file: reads its sidecar metadata, recomputes
    /// SHA-256, returns the outcome enum. Pure / static — same primitive used
    /// internally by <see cref="RunPlan"/> and externally by
    /// <see cref="CacheRepairer"/> to drive its repair dispatch.
    /// </summary>
    public static VerifyOutcome Inspect(string filePath)
    {
        if (!DataPoolFileMeta.TryRead(filePath, out var meta))
        {
            return VerifyOutcome.NoMetadata;
        }

        try
        {
            var info = new FileInfo(filePath);
            if (info.Length != meta.Size)
            {
                return VerifyOutcome.SizeMismatch;
            }

            using var stream = File.OpenRead(filePath);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return hash.Equals(meta.Sha256, StringComparison.OrdinalIgnoreCase)
                ? VerifyOutcome.Ok
                : VerifyOutcome.HashMismatch;
        }
        catch
        {
            return VerifyOutcome.IoError;
        }
    }
}

public readonly record struct VerifyTarget(string Symbol, string Path);

public enum VerifyOutcome
{
    Ok,
    NoMetadata,
    SizeMismatch,
    HashMismatch,
    IoError
}

public sealed record VerifyMismatch(string Symbol, string Path, VerifyOutcome Outcome);

/// <summary>
/// Outcome of the remote (source-side) check on a single file. Only applies
/// when <c>--remote</c> was passed and the local check returned
/// <see cref="VerifyOutcome.Ok"/>; otherwise this stays <see cref="NotChecked"/>.
/// </summary>
public enum RemoteVerifyOutcome
{
    NotChecked,
    RemoteOk,
    RemoteSizeMismatch,
    RemoteHashMismatch,
    RemoteUnreachable
}

public sealed record RemoteVerifyMismatch(
    string Symbol,
    string Path,
    RemoteVerifyOutcome Outcome,
    long? LocalSize,
    long? RemoteSize);

public sealed class CacheVerifyReport
{
    /// <summary>
    /// Cap on the per-mismatch detail list to keep rendered output manageable
    /// even when a pool has thousands of bad files. Aggregate counts are unaffected.
    /// </summary>
    public const int MaxMismatchesShown = 100;

    public required string PoolPath { get; init; }
    public bool PoolExists { get; init; }
    public int FilesPlanned { get; init; }
    public bool RemoteCheckPerformed { get; init; }
    public bool RemoteByteExact { get; init; }
    public List<SymbolVerifyReport> Symbols { get; } = new();
    public List<VerifyMismatch> Mismatches { get; } = new();
    public List<RemoteVerifyMismatch> RemoteMismatches { get; } = new();

    public int TotalOk => Symbols.Sum(s => s.OkCount);
    public int TotalNoMetadata => Symbols.Sum(s => s.NoMetadataCount);
    public int TotalSizeMismatch => Symbols.Sum(s => s.SizeMismatchCount);
    public int TotalHashMismatch => Symbols.Sum(s => s.HashMismatchCount);
    public int TotalIoError => Symbols.Sum(s => s.IoErrorCount);
    public int TotalProblems => TotalNoMetadata + TotalSizeMismatch + TotalHashMismatch + TotalIoError;

    // Remote-side aggregates. All zero when RemoteCheckPerformed=false.
    public int TotalRemoteOk => Symbols.Sum(s => s.RemoteOkCount);
    public int TotalRemoteSizeMismatch => Symbols.Sum(s => s.RemoteSizeMismatchCount);
    public int TotalRemoteHashMismatch => Symbols.Sum(s => s.RemoteHashMismatchCount);
    public int TotalRemoteUnreachable => Symbols.Sum(s => s.RemoteUnreachableCount);
    public int TotalRemoteChecked => TotalRemoteOk + TotalRemoteSizeMismatch + TotalRemoteHashMismatch + TotalRemoteUnreachable;
    public int TotalRemoteProblems => TotalRemoteSizeMismatch + TotalRemoteHashMismatch + TotalRemoteUnreachable;

    /// <summary>
    /// True when the pool exists, every cached file passed local verification,
    /// AND (if a remote check was performed) every file also passed the remote
    /// check. A locally-clean pool with --remote drift is NOT AllClean.
    /// </summary>
    public bool AllClean =>
        PoolExists
        && TotalProblems == 0
        && FilesPlanned > 0
        && (!RemoteCheckPerformed || TotalRemoteProblems == 0);

    public string Render()
    {
        var sb = new StringBuilder();
        if (!PoolExists)
        {
            sb.AppendLine($"Pool path does not exist: {PoolPath}");
            return sb.ToString();
        }

        sb.AppendLine($"Pool: {PoolPath}");
        sb.AppendLine($"Files verified: {FilesPlanned:N0}");
        sb.AppendLine($"  Ok:             {TotalOk,8:N0}");
        if (TotalNoMetadata > 0)   sb.AppendLine($"  No metadata:    {TotalNoMetadata,8:N0}  (sidecar .meta.json missing)");
        if (TotalSizeMismatch > 0) sb.AppendLine($"  Size mismatch:  {TotalSizeMismatch,8:N0}");
        if (TotalHashMismatch > 0) sb.AppendLine($"  Hash mismatch:  {TotalHashMismatch,8:N0}  (file content differs from recorded SHA-256)");
        if (TotalIoError > 0)      sb.AppendLine($"  I/O error:      {TotalIoError,8:N0}");

        if (Symbols.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Per-symbol:");
            sb.AppendLine($"  {"Symbol",-10} {"Files",10} {"Ok",10} {"NoMeta",8} {"Size",6} {"Hash",6} {"IO",4}");
            foreach (var s in Symbols)
            {
                sb.AppendLine($"  {s.Symbol,-10} {s.TotalCount,10:N0} {s.OkCount,10:N0} {s.NoMetadataCount,8:N0} {s.SizeMismatchCount,6:N0} {s.HashMismatchCount,6:N0} {s.IoErrorCount,4:N0}");
            }
        }

        if (Mismatches.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Mismatches (first {Mismatches.Count:N0}):");
            foreach (var m in Mismatches)
            {
                sb.AppendLine($"  [{m.Outcome,-13}] {m.Path}");
            }
            if (TotalProblems > Mismatches.Count)
            {
                sb.AppendLine($"  ... {TotalProblems - Mismatches.Count:N0} more not shown");
            }
        }

        // Remote (source-side) section, only when --remote was passed.
        if (RemoteCheckPerformed)
        {
            sb.AppendLine();
            var modeLabel = RemoteByteExact ? "byte-exact" : "size-only";
            sb.AppendLine($"Remote drift check ({modeLabel} vs Dukascopy):");
            sb.AppendLine($"  Files probed:    {TotalRemoteChecked,8:N0}  (locally-bad files skipped)");
            sb.AppendLine($"  Remote ok:       {TotalRemoteOk,8:N0}");
            if (TotalRemoteSizeMismatch > 0) sb.AppendLine($"  Size drift:      {TotalRemoteSizeMismatch,8:N0}  (Dukascopy size differs from cache)");
            if (TotalRemoteHashMismatch > 0) sb.AppendLine($"  Hash drift:      {TotalRemoteHashMismatch,8:N0}  (Dukascopy bytes differ; same length)");
            if (TotalRemoteUnreachable > 0)  sb.AppendLine($"  Unreachable:     {TotalRemoteUnreachable,8:N0}  (404 / network failure)");

            if (RemoteMismatches.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Remote drift detail (first {RemoteMismatches.Count:N0}):");
                foreach (var m in RemoteMismatches)
                {
                    var sizes = m.LocalSize is long ls && m.RemoteSize is long rs
                        ? $"local {ls,8:N0}B / remote {rs,8:N0}B"
                        : "size unknown";
                    sb.AppendLine($"  [{m.Outcome,-19}] {sizes}  {m.Path}");
                }
                if (TotalRemoteProblems > RemoteMismatches.Count)
                {
                    sb.AppendLine($"  ... {TotalRemoteProblems - RemoteMismatches.Count:N0} more not shown");
                }
            }
        }

        sb.AppendLine();
        if (FilesPlanned == 0)
        {
            sb.AppendLine("No .bi5 files found in pool — nothing to verify.");
        }
        else if (TotalProblems == 0 && (!RemoteCheckPerformed || TotalRemoteProblems == 0))
        {
            sb.AppendLine(RemoteCheckPerformed
                ? "All cached files verified clean against local sidecars and Dukascopy."
                : "All cached files verified clean.");
        }
        else
        {
            if (TotalProblems > 0)
            {
                sb.AppendLine($"{TotalProblems:N0} local problem(s) found. Run `cache repair` to fix or `cache cleanup` to remove the dead files.");
            }
            if (RemoteCheckPerformed && TotalRemoteProblems > 0)
            {
                sb.AppendLine($"{TotalRemoteProblems:N0} remote drift issue(s). Run `cache repair` to refetch the affected files.");
            }
        }

        return sb.ToString();
    }
}

public sealed class SymbolVerifyReport
{
    public required string Symbol { get; init; }
    public int OkCount { get; private set; }
    public int NoMetadataCount { get; private set; }
    public int SizeMismatchCount { get; private set; }
    public int HashMismatchCount { get; private set; }
    public int IoErrorCount { get; private set; }

    // Remote-side counters; zero unless --remote was passed.
    public int RemoteOkCount { get; private set; }
    public int RemoteSizeMismatchCount { get; private set; }
    public int RemoteHashMismatchCount { get; private set; }
    public int RemoteUnreachableCount { get; private set; }
    public int RemoteCheckedCount =>
        RemoteOkCount + RemoteSizeMismatchCount + RemoteHashMismatchCount + RemoteUnreachableCount;

    public int TotalCount =>
        OkCount + NoMetadataCount + SizeMismatchCount + HashMismatchCount + IoErrorCount;

    public void Increment(VerifyOutcome outcome)
    {
        switch (outcome)
        {
            case VerifyOutcome.Ok: OkCount++; break;
            case VerifyOutcome.NoMetadata: NoMetadataCount++; break;
            case VerifyOutcome.SizeMismatch: SizeMismatchCount++; break;
            case VerifyOutcome.HashMismatch: HashMismatchCount++; break;
            case VerifyOutcome.IoError: IoErrorCount++; break;
        }
    }

    public void IncrementRemote(RemoteVerifyOutcome outcome)
    {
        switch (outcome)
        {
            case RemoteVerifyOutcome.RemoteOk: RemoteOkCount++; break;
            case RemoteVerifyOutcome.RemoteSizeMismatch: RemoteSizeMismatchCount++; break;
            case RemoteVerifyOutcome.RemoteHashMismatch: RemoteHashMismatchCount++; break;
            case RemoteVerifyOutcome.RemoteUnreachable: RemoteUnreachableCount++; break;
            // NotChecked: do nothing — this file wasn't probed remotely.
        }
    }
}
