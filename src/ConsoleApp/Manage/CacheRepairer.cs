using System.Globalization;
using System.Text;
using HistoricalData.DataPool;

namespace HistoricalData.Manage;

/// <summary>
/// Auto-fixes problems surfaced by <see cref="CacheVerifier"/>. For each
/// .bi5 file in the pool it inspects the file's checksum status and
/// dispatches one of:
///
/// <list type="bullet">
///   <item><description><b>Refetch</b> — re-downloads the file from
///   Dukascopy via <see cref="IFileRefetcher"/>. Default for
///   SizeMismatch, HashMismatch, and (per the user-chosen default) NoMetadata.
///   Sidecar is rewritten by the fetch.</description></item>
///   <item><description><b>RegenerateSidecar</b> — trusts the local file,
///   computes its current SHA-256 and writes a new <c>.meta.json</c>. Used
///   for NoMetadata when <c>--trust-existing</c> is passed. No network.</description></item>
///   <item><description><b>Skip</b> — IoError or otherwise non-actionable.
///   Reported but untouched.</description></item>
/// </list>
///
/// One pass over the pool: every file is inspected; only problem files do
/// any I/O work beyond the hash. A single <see cref="FilesProcessed"/>
/// counter drives the progress bar regardless of whether each file
/// triggered a repair.
/// </summary>
public sealed class CacheRepairer
{
    private readonly string _poolPath;
    private readonly IFileRefetcher _refetcher;
    private long _filesProcessed;

    public CacheRepairer(string poolPath, IFileRefetcher refetcher)
    {
        _poolPath = poolPath ?? throw new ArgumentNullException(nameof(poolPath));
        _refetcher = refetcher ?? throw new ArgumentNullException(nameof(refetcher));
    }

    /// <summary>
    /// Files that have been inspected (success or repair attempt).
    /// Atomically incremented; safe to poll from another thread.
    /// </summary>
    public long FilesProcessed => Interlocked.Read(ref _filesProcessed);

    /// <summary>
    /// Convenience: enumerate then run in a single call. No progress bar.
    /// Real callers should call <see cref="PlanFiles"/> + <see cref="RunPlanAsync"/>
    /// separately so a progress bar can know the total upfront.
    /// </summary>
    public Task<CacheRepairReport> RepairAsync(
        IReadOnlyCollection<string>? symbolFilter,
        bool dryRun,
        bool trustExisting,
        CancellationToken cancellationToken = default)
    {
        var plan = PlanFiles(symbolFilter);
        return RunPlanAsync(plan, dryRun, trustExisting, cancellationToken);
    }

    /// <summary>
    /// Pass 1: enumerate every .bi5 in the pool matching the filter.
    /// Delegates to <see cref="CacheVerifier.PlanFiles"/> so the two pillars
    /// agree on what counts as a "cached file."
    /// </summary>
    public IReadOnlyList<VerifyTarget> PlanFiles(IReadOnlyCollection<string>? symbolFilter = null) =>
        new CacheVerifier(_poolPath).PlanFiles(symbolFilter);

    /// <summary>
    /// Pass 2: inspect each file, repair the problems. Resets and increments
    /// <see cref="FilesProcessed"/> as it goes. Throws
    /// <see cref="OperationCanceledException"/> on cancellation; the report
    /// returned reflects only the work completed before cancellation (i.e. the
    /// caller should still inspect partial progress).
    /// </summary>
    public async Task<CacheRepairReport> RunPlanAsync(
        IReadOnlyList<VerifyTarget> plan,
        bool dryRun,
        bool trustExisting,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _filesProcessed, 0);

        var report = new CacheRepairReport
        {
            PoolPath = _poolPath,
            DryRun = dryRun,
            TrustExisting = trustExisting,
            FilesPlanned = plan.Count
        };

        foreach (var target in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = CacheVerifier.Inspect(target.Path);
            if (outcome != VerifyOutcome.Ok)
            {
                var action = DecideAction(outcome, trustExisting);
                var result = dryRun
                    ? RepairResult.NotAttempted
                    : await ExecuteAsync(target, action, cancellationToken);
                report.Tasks.Add(new RepairTaskReport(target.Symbol, target.Path, outcome, action, result));
            }

            Interlocked.Increment(ref _filesProcessed);
        }

        return report;
    }

    private static RepairAction DecideAction(VerifyOutcome outcome, bool trustExisting)
    {
        return outcome switch
        {
            VerifyOutcome.NoMetadata => trustExisting
                ? RepairAction.RegenerateSidecar
                : RepairAction.Refetch,
            VerifyOutcome.SizeMismatch => RepairAction.Refetch,
            VerifyOutcome.HashMismatch => RepairAction.Refetch,
            VerifyOutcome.IoError => RepairAction.Skip,
            _ => RepairAction.Skip
        };
    }

    private async Task<RepairResult> ExecuteAsync(VerifyTarget target, RepairAction action, CancellationToken ct)
    {
        switch (action)
        {
            case RepairAction.RegenerateSidecar:
                try
                {
                    DataPoolFileMeta.Write(target.Path);
                    return RepairResult.SidecarRegenerated;
                }
                catch
                {
                    return RepairResult.Skipped;
                }

            case RepairAction.Refetch:
                if (!TryParseCacheFilePath(target.Path, out var symbol, out var year, out var month, out var day, out var fileName))
                {
                    return RepairResult.Skipped;
                }

                // Use the symbol from the path itself (matches what the Verifier emitted).
                var ok = await _refetcher.RefetchAsync(symbol, year, month, day, fileName, ct);
                return ok ? RepairResult.Refetched : RepairResult.RefetchFailed;

            default:
                return RepairResult.Skipped;
        }
    }

    /// <summary>
    /// Parses the cache layout convention <c>&lt;pool&gt;/&lt;symbol&gt;/&lt;yyyy&gt;/&lt;dukaMonth&gt;/&lt;dd&gt;/&lt;file&gt;</c>
    /// out of an absolute file path. The Manage pillar already validates
    /// these segments are integer-parseable in <see cref="CacheVerifier.PlanFiles"/>,
    /// so this should always succeed for files that came out of the planner.
    /// </summary>
    internal static bool TryParseCacheFilePath(
        string filePath,
        out string symbol,
        out int year,
        out int dukaMonth,
        out int day,
        out string fileName)
    {
        symbol = string.Empty;
        year = 0;
        dukaMonth = 0;
        day = 0;
        fileName = Path.GetFileName(filePath);

        var dayDir = Path.GetDirectoryName(filePath);
        if (dayDir is null) return false;
        if (!int.TryParse(Path.GetFileName(dayDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out day)) return false;

        var monthDir = Path.GetDirectoryName(dayDir);
        if (monthDir is null) return false;
        if (!int.TryParse(Path.GetFileName(monthDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out dukaMonth)) return false;

        var yearDir = Path.GetDirectoryName(monthDir);
        if (yearDir is null) return false;
        if (!int.TryParse(Path.GetFileName(yearDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) return false;

        var symbolDir = Path.GetDirectoryName(yearDir);
        if (symbolDir is null) return false;
        symbol = Path.GetFileName(symbolDir);

        return !string.IsNullOrWhiteSpace(symbol) && !string.IsNullOrWhiteSpace(fileName);
    }
}

public enum RepairAction
{
    Skip,
    RegenerateSidecar,
    Refetch
}

public enum RepairResult
{
    NotAttempted,        // dry-run preview, nothing was done
    SidecarRegenerated,  // success: .meta.json written from current bytes
    Refetched,           // success: file re-downloaded, sidecar rewritten by refetcher
    RefetchFailed,       // refetch attempted, network 404/timeout, file untouched
    Skipped              // IoError or non-parseable path; reported but no action
}

public sealed record RepairTaskReport(
    string Symbol,
    string Path,
    VerifyOutcome OriginalOutcome,
    RepairAction PlannedAction,
    RepairResult Result);

public sealed class CacheRepairReport
{
    /// <summary>
    /// Cap on the per-task detail list to keep rendered output manageable
    /// even when a pool has thousands of bad files. Aggregate counts are
    /// always exact regardless of this cap.
    /// </summary>
    public const int MaxTasksShown = 100;

    public required string PoolPath { get; init; }
    public bool DryRun { get; init; }
    public bool TrustExisting { get; init; }
    public int FilesPlanned { get; init; }
    public List<RepairTaskReport> Tasks { get; } = new();

    public int ProblemsFound => Tasks.Count;
    public int SidecarRegenerated => Tasks.Count(t => t.Result == RepairResult.SidecarRegenerated);
    public int Refetched => Tasks.Count(t => t.Result == RepairResult.Refetched);
    public int RefetchFailed => Tasks.Count(t => t.Result == RepairResult.RefetchFailed);
    public int Skipped => Tasks.Count(t => t.Result == RepairResult.Skipped);
    public int Pending => Tasks.Count(t => t.Result == RepairResult.NotAttempted);
    public int Repaired => SidecarRegenerated + Refetched;
    public int Outstanding => RefetchFailed + Skipped;

    /// <summary>
    /// True iff every problem found was repaired. Dry-runs never report success
    /// (nothing was actually changed).
    /// </summary>
    public bool AllResolved => !DryRun && ProblemsFound > 0 && Outstanding == 0 && Repaired == ProblemsFound;

    public string Render()
    {
        var sb = new StringBuilder();
        if (DryRun)
        {
            sb.AppendLine("DRY RUN — no changes made.");
        }

        sb.AppendLine($"Pool: {PoolPath}");
        sb.AppendLine($"Files inspected: {FilesPlanned:N0}");

        if (ProblemsFound == 0)
        {
            sb.AppendLine("No problems found.");
            return sb.ToString();
        }

        sb.AppendLine($"Problems found: {ProblemsFound:N0}");
        if (DryRun)
        {
            var wouldRefetch = Tasks.Count(t => t.PlannedAction == RepairAction.Refetch);
            var wouldRegen = Tasks.Count(t => t.PlannedAction == RepairAction.RegenerateSidecar);
            var wouldSkip = Tasks.Count(t => t.PlannedAction == RepairAction.Skip);
            if (wouldRefetch > 0) sb.AppendLine($"  Would refetch:           {wouldRefetch,8:N0}");
            if (wouldRegen > 0)   sb.AppendLine($"  Would regenerate sidecar:{wouldRegen,8:N0}");
            if (wouldSkip > 0)    sb.AppendLine($"  Would skip (IoError):    {wouldSkip,8:N0}");
        }
        else
        {
            if (Refetched > 0)          sb.AppendLine($"  Refetched:           {Refetched,8:N0}");
            if (SidecarRegenerated > 0) sb.AppendLine($"  Sidecar regenerated: {SidecarRegenerated,8:N0}");
            if (RefetchFailed > 0)      sb.AppendLine($"  Refetch failed:      {RefetchFailed,8:N0}  (file untouched; source 404 or network)");
            if (Skipped > 0)            sb.AppendLine($"  Skipped:             {Skipped,8:N0}  (IoError / unparseable path)");
        }

        if (Tasks.Count > 0)
        {
            sb.AppendLine();
            var shown = Math.Min(Tasks.Count, MaxTasksShown);
            sb.AppendLine($"Per-file ({shown:N0} of {Tasks.Count:N0}):");
            for (var i = 0; i < shown; i++)
            {
                var t = Tasks[i];
                sb.AppendLine($"  [{t.OriginalOutcome,-13}] -> [{t.Result,-19}] {t.Path}");
            }
            if (Tasks.Count > shown)
            {
                sb.AppendLine($"  ... {Tasks.Count - shown:N0} more not shown");
            }
        }

        sb.AppendLine();
        if (DryRun)
        {
            sb.AppendLine("Re-run without --dry-run to apply.");
        }
        else if (Outstanding == 0)
        {
            sb.AppendLine("All problems resolved.");
        }
        else
        {
            sb.AppendLine($"{Outstanding:N0} file(s) still bad. Re-run `cache verify` to confirm and investigate.");
        }

        return sb.ToString();
    }
}
