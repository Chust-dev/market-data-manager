using HistoricalData.Commands.Options;
using HistoricalData.Download;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache catchup` — refresh the rolling N-day window of the .bi5 cache.
/// Convenient shorthand for "I want my cache up-to-date for the last
/// N days." Defaults to a 60-day window; override with --window N.
///
/// Forces RefreshCache=true and RecentRefreshDays = window + 1, so the
/// entire requested window is re-fetched from Dukascopy on each run
/// regardless of cache state. That's the entire point of catchup —
/// Dukascopy occasionally amends recent ticks within a few weeks of
/// publication; the rolling refresh captures those amendments.
///
/// `--no-refresh` is semantically nonsense in this context (catchup
/// without refreshing is a contradiction) and is silently overridden,
/// with a one-line note pointing the user at `cache update --no-refresh`
/// for incremental-refresh use cases.
///
/// Usage:
///   cache catchup --instrument EURUSD                       (last 60 days, default)
///   cache catchup --symbols all                             (all symbols, last 60 days)
///   cache catchup --instrument USDJPY --window 30           (last 30 days)
///   cache catchup --symbols all --window 90 --no-prompt     (cron-friendly)
/// </summary>
public sealed class CacheCatchupCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var window = CommonParsingHelpers.ParseWindow(argMap);

        // Compute window endpoints in UTC, snapped to date boundaries.
        // +1 day on the end gives us any hours Dukascopy has just published.
        var nowUtc = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(nowUtc.UtcDateTime.AddDays(-window).Date, TimeSpan.Zero);
        var end = new DateTimeOffset(nowUtc.UtcDateTime.AddDays(1).Date, TimeSpan.Zero);

        // Build CacheUpdateOptions from args, then override the dates and refresh
        // policy to enforce catchup semantics (whole window refreshed every run).
        var options = CacheUpdateOptions.FromArgs(argMap) with
        {
            Start = start,
            End = end,
            RefreshCache = true,
            RecentRefreshDays = window + 1
        };

        if (!options.Quiet)
        {
            Console.WriteLine($"Catching up last {window} days ({start:yyyy-MM-dd} → {end:yyyy-MM-dd} UTC)");

            if (argMap.ContainsKey("no-refresh"))
            {
                Console.WriteLine("Note: --no-refresh ignored. `cache catchup` always refreshes its window.");
                Console.WriteLine("      Use `cache update --no-refresh` if you need incremental refresh.");
            }
        }

        return new Downloader().RunAsync(options);
    }
}
