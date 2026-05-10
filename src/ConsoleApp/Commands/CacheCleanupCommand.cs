using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache cleanup` — removes useless files from the pool: zero-byte .bi5
/// downloads, orphan .meta.json sidecars (no matching data file), and
/// leftover .tmp files. After deletion, prunes any now-empty
/// day/month/year directories so the tree stays tidy.
///
/// **Destructive by default.** Running <c>cache cleanup</c> with no flags
/// deletes the matching files. Pass <c>--dry-run</c> first to preview the
/// plan. Always run <c>--dry-run</c> on a fresh pool before the real run.
///
/// Filters: <c>--instrument SYMBOL</c> or <c>--symbols SYM1,SYM2,...</c>.
/// <c>--quiet</c> silences the progress bar; Ctrl+C cancels mid-run.
///
/// Exit code: 0 if the run finished (whether dry-run, no-op, or completed
/// deletions), 1 if any file deletion threw or the run was cancelled.
/// </summary>
public sealed class CacheCleanupCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheCleanupOptions.FromArgs(argMap);
        return Task.FromResult(Program.RunCleanup(options));
    }
}
