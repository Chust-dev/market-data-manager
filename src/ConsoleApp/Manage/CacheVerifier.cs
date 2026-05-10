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
    /// Pass 2: verify each file in the plan. Resets and increments
    /// <see cref="FilesProcessed"/> as it goes. Throws
    /// <see cref="OperationCanceledException"/> on cancellation; partial work
    /// is discarded (the caller decides how to surface that).
    /// </summary>
    public CacheVerifyReport RunPlan(IReadOnlyList<VerifyTarget> plan, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _filesProcessed, 0);

        var report = new CacheVerifyReport
        {
            PoolPath = _poolPath,
            PoolExists = Directory.Exists(_poolPath),
            FilesPlanned = plan.Count
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

            var outcome = Inspect(target.Path);
            symbolReport.Increment(outcome);

            if (outcome != VerifyOutcome.Ok && report.Mismatches.Count < CacheVerifyReport.MaxMismatchesShown)
            {
                report.Mismatches.Add(new VerifyMismatch(target.Symbol, target.Path, outcome));
            }

            Interlocked.Increment(ref _filesProcessed);
        }

        report.Symbols.AddRange(bySymbol.Values.OrderBy(s => s.Symbol, StringComparer.OrdinalIgnoreCase));
        return report;
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
    public List<SymbolVerifyReport> Symbols { get; } = new();
    public List<VerifyMismatch> Mismatches { get; } = new();

    public int TotalOk => Symbols.Sum(s => s.OkCount);
    public int TotalNoMetadata => Symbols.Sum(s => s.NoMetadataCount);
    public int TotalSizeMismatch => Symbols.Sum(s => s.SizeMismatchCount);
    public int TotalHashMismatch => Symbols.Sum(s => s.HashMismatchCount);
    public int TotalIoError => Symbols.Sum(s => s.IoErrorCount);
    public int TotalProblems => TotalNoMetadata + TotalSizeMismatch + TotalHashMismatch + TotalIoError;
    public bool AllClean => PoolExists && TotalProblems == 0 && FilesPlanned > 0;

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

        sb.AppendLine();
        if (FilesPlanned == 0)
        {
            sb.AppendLine("No .bi5 files found in pool — nothing to verify.");
        }
        else if (TotalProblems == 0)
        {
            sb.AppendLine("All cached files verified clean.");
        }
        else
        {
            sb.AppendLine($"{TotalProblems:N0} problem(s) found. Delete affected files and re-run `cache update` to refetch.");
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
}
