using System.Globalization;
using System.Text;

namespace HistoricalData.Manage;

/// <summary>
/// Removes files from the .bi5 pool that are definitively useless: zero-byte
/// downloads, orphan .meta.json sidecars (no matching .bi5), and leftover
/// .tmp files from interrupted writes. After deletion, walks back up the
/// directory tree and removes any now-empty day / month / year folders so
/// the pool stays tidy.
///
/// Deliberately does NOT delete:
/// <list type="bullet">
///   <item><description>hash-mismatched .bi5 files — that's
///   <see cref="CacheRepairer"/>'s job. Cleanup is for <i>useless</i> files,
///   not <i>broken</i> ones.</description></item>
///   <item><description>files for symbols not in <c>instruments.json</c> —
///   the user might be tracking them deliberately.</description></item>
///   <item><description>non-symbol subfolders like <c>Exports/</c> — they're
///   not pool data and we shouldn't touch them.</description></item>
/// </list>
///
/// Two-phase API for progress reporting: <see cref="PlanCleanup"/> walks the
/// pool and builds the deletion list (fast), <see cref="RunPlan"/> executes it
/// (fast unless the pool has thousands of dead files).
/// </summary>
public sealed class CacheCleaner
{
    private const string Bi5Suffix = ".bi5";
    private const string MetaSuffix = ".meta.json";
    private const string TmpSuffix = ".tmp";

    private readonly string _poolPath;
    private long _filesProcessed;

    public CacheCleaner(string poolPath)
    {
        _poolPath = poolPath ?? throw new ArgumentNullException(nameof(poolPath));
    }

    public long FilesProcessed => Interlocked.Read(ref _filesProcessed);

    /// <summary>
    /// One-shot convenience: enumerate then run.
    /// </summary>
    public CacheCleanupReport Cleanup(
        IReadOnlyCollection<string>? symbolFilter,
        bool dryRun,
        CancellationToken cancellationToken = default)
    {
        var plan = PlanCleanup(symbolFilter);
        return RunPlan(plan, dryRun, cancellationToken);
    }

    /// <summary>
    /// Pass 1: walk the pool and return everything that should be removed.
    /// Pure — does not delete anything. Tagged with the symbol so the report
    /// can group results, plus the byte size so the report can show
    /// "reclaimable space."
    /// </summary>
    public IReadOnlyList<CleanupTarget> PlanCleanup(IReadOnlyCollection<string>? symbolFilter = null)
    {
        var plan = new List<CleanupTarget>();
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

            CollectFromSymbolDir(symbol, symbolDir, plan);
        }

        return plan;
    }

    private static void CollectFromSymbolDir(string symbol, string symbolDir, List<CleanupTarget> plan)
    {
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

                    CollectFromDayDir(symbol, dayDir, plan);
                }
            }
        }
    }

    private static void CollectFromDayDir(string symbol, string dayDir, List<CleanupTarget> plan)
    {
        // Snapshot the file list once so we can do the orphan-sidecar check by lookup.
        var files = Directory.EnumerateFiles(dayDir).ToList();
        var fileSet = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var info = new FileInfo(path);

            if (name.EndsWith(TmpSuffix, StringComparison.OrdinalIgnoreCase))
            {
                plan.Add(new CleanupTarget(symbol, path, CleanupReason.OrphanTmp, info.Length));
                continue;
            }

            if (name.EndsWith(MetaSuffix, StringComparison.OrdinalIgnoreCase))
            {
                // Orphan sidecar = the .bi5 it would describe is missing.
                var dataPath = path[..^MetaSuffix.Length];
                if (!fileSet.Contains(dataPath))
                {
                    plan.Add(new CleanupTarget(symbol, path, CleanupReason.OrphanSidecar, info.Length));
                }
                continue;
            }

            if (name.EndsWith(Bi5Suffix, StringComparison.OrdinalIgnoreCase) && info.Length == 0)
            {
                plan.Add(new CleanupTarget(symbol, path, CleanupReason.ZeroByte, 0));
                // Also remove the orphan sidecar that will be left behind.
                var metaPath = path + MetaSuffix;
                if (fileSet.Contains(metaPath))
                {
                    var metaInfo = new FileInfo(metaPath);
                    plan.Add(new CleanupTarget(symbol, metaPath, CleanupReason.OrphanSidecar, metaInfo.Length));
                }
            }
        }
    }

    /// <summary>
    /// Pass 2: process the cleanup plan. In dry-run mode, just records what
    /// would be deleted. With <paramref name="dryRun"/>=false, actually deletes
    /// files and walks back up the directory tree removing now-empty
    /// day/month/year folders. Resets and increments
    /// <see cref="FilesProcessed"/> as it goes.
    /// </summary>
    public CacheCleanupReport RunPlan(IReadOnlyList<CleanupTarget> plan, bool dryRun, CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _filesProcessed, 0);

        var report = new CacheCleanupReport
        {
            PoolPath = _poolPath,
            DryRun = dryRun,
            FilesPlanned = plan.Count
        };

        // Track parent dirs touched so we can prune empty ones at the end.
        var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (dryRun)
            {
                report.Targets.Add(target);
                Interlocked.Increment(ref _filesProcessed);
                continue;
            }

            try
            {
                File.Delete(target.Path);
                report.Targets.Add(target);
                var dir = Path.GetDirectoryName(target.Path);
                if (!string.IsNullOrEmpty(dir))
                {
                    touchedDirs.Add(dir);
                }
            }
            catch (Exception ex)
            {
                report.DeletionErrors.Add($"{target.Path}: {ex.Message}");
            }

            Interlocked.Increment(ref _filesProcessed);
        }

        if (!dryRun)
        {
            report.EmptyDirsRemoved = PruneEmptyDirs(touchedDirs);
        }

        return report;
    }

    /// <summary>
    /// Removes day/month/year directories that became empty after deletion.
    /// Walks up the tree from each touched dir, stopping at the symbol level
    /// (we never delete the symbol root or the pool root, even if they're
    /// empty — that's a much bigger decision than cleanup should make).
    /// </summary>
    private int PruneEmptyDirs(HashSet<string> touchedDirs)
    {
        var removed = 0;
        // Sort longest path first so day dirs prune before their month parents.
        foreach (var startDir in touchedDirs.OrderByDescending(d => d.Length))
        {
            var current = startDir;
            for (var depth = 0; depth < 3 && current is not null; depth++)
            {
                if (!Directory.Exists(current))
                {
                    current = Path.GetDirectoryName(current);
                    continue;
                }

                if (!IsEmptyDir(current))
                {
                    break;
                }

                // Don't climb above the symbol level (== 4 segments below the pool root).
                var relative = Path.GetRelativePath(_poolPath, current);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length <= 1)
                {
                    break;
                }

                try
                {
                    Directory.Delete(current);
                    removed++;
                }
                catch
                {
                    break;
                }
                current = Path.GetDirectoryName(current);
            }
        }

        return removed;
    }

    private static bool IsEmptyDir(string dir)
    {
        try
        {
            using var en = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
            return !en.MoveNext();
        }
        catch
        {
            return false;
        }
    }
}

public enum CleanupReason
{
    ZeroByte,        // .bi5 file with size 0
    OrphanSidecar,   // .meta.json with no matching .bi5
    OrphanTmp        // leftover .tmp file
}

public readonly record struct CleanupTarget(string Symbol, string Path, CleanupReason Reason, long Bytes);

public sealed class CacheCleanupReport
{
    public const int MaxTargetsShown = 100;

    public required string PoolPath { get; init; }
    public bool DryRun { get; init; }
    public int FilesPlanned { get; init; }
    public List<CleanupTarget> Targets { get; } = new();
    public List<string> DeletionErrors { get; } = new();
    public int EmptyDirsRemoved { get; set; }

    public int ZeroByteCount => Targets.Count(t => t.Reason == CleanupReason.ZeroByte);
    public int OrphanSidecarCount => Targets.Count(t => t.Reason == CleanupReason.OrphanSidecar);
    public int OrphanTmpCount => Targets.Count(t => t.Reason == CleanupReason.OrphanTmp);
    public long TotalBytes => Targets.Sum(t => t.Bytes);
    public bool AnyFailures => DeletionErrors.Count > 0;

    public string Render()
    {
        var sb = new StringBuilder();

        if (DryRun)
        {
            sb.AppendLine("DRY RUN — no changes made.");
        }
        else
        {
            sb.AppendLine("Cleanup applied — files deleted.");
        }

        sb.AppendLine($"Pool: {PoolPath}");

        if (Targets.Count == 0)
        {
            sb.AppendLine("Nothing to remove.");
            return sb.ToString();
        }

        sb.AppendLine($"Cleanup candidates: {Targets.Count:N0}");
        if (ZeroByteCount > 0)        sb.AppendLine($"  Zero-byte .bi5 files: {ZeroByteCount,8:N0}");
        if (OrphanSidecarCount > 0)   sb.AppendLine($"  Orphan sidecars:      {OrphanSidecarCount,8:N0}");
        if (OrphanTmpCount > 0)       sb.AppendLine($"  Orphan .tmp files:    {OrphanTmpCount,8:N0}");
        sb.AppendLine($"Reclaimable space:    {FormatBytes(TotalBytes)}");

        if (Targets.Count > 0)
        {
            sb.AppendLine();
            var shown = Math.Min(Targets.Count, MaxTargetsShown);
            sb.AppendLine($"Per-file ({shown:N0} of {Targets.Count:N0}):");
            for (var i = 0; i < shown; i++)
            {
                var t = Targets[i];
                sb.AppendLine($"  [{t.Reason,-13}] {t.Path}");
            }
            if (Targets.Count > shown)
            {
                sb.AppendLine($"  ... {Targets.Count - shown:N0} more not shown");
            }
        }

        if (!DryRun && EmptyDirsRemoved > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Empty directories pruned: {EmptyDirsRemoved:N0}");
        }

        if (AnyFailures)
        {
            sb.AppendLine();
            sb.AppendLine($"Failed deletions: {DeletionErrors.Count:N0}");
            foreach (var err in DeletionErrors.Take(10))
            {
                sb.AppendLine($"  {err}");
            }
            if (DeletionErrors.Count > 10)
            {
                sb.AppendLine($"  ... {DeletionErrors.Count - 10:N0} more not shown");
            }
        }

        sb.AppendLine();
        if (DryRun)
        {
            sb.AppendLine("Re-run without --dry-run to actually delete these files.");
        }

        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F1} MB";
        if (bytes >= KB) return $"{bytes / KB:F0} KB";
        return $"{bytes} B";
    }
}
