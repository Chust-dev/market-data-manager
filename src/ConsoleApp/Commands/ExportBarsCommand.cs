using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `export bars` — read the .bi5 pool (no network) and write MT5-compatible
/// bar files (CSV and/or HST) for the requested instrument and timeframe.
///
/// This is the cache-projection counterpart to `cache update`: the cache must
/// already be filled, this command only reads from it. Missing hours are
/// treated as data gaps and silently skipped (matching the cache-only mode
/// behaviour added in earlier commits).
///
/// Args (all optional):
///   --instrument SYMBOL or --symbols SYMBOL1,SYMBOL2,...|all
///   --start ISO8601    --end ISO8601
///   --timeframe m1|m5|m15|m30|h1|h4|h6|d1|w1|mn1|m&lt;minutes&gt;
///   --format csv|csv+hst   (default csv+hst)
///   --offset +HH:MM
///   --pool PATH    --output PATH
///   --no-prompt
///   --quiet
/// </summary>
public sealed class ExportBarsCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = AppOptions.FromArgs(argMap);

        // Export-bars invariants: cache-only (no network), no tick CSV side-effect.
        // Force these regardless of what the user passed.
        options = options with
        {
            DownloadMode = DownloadMode.TickToM1,
            RefreshCache = false,
            RecentRefreshDays = 0,
            VerifyChecksum = false,
            RepairGaps = false,
            ValidateM1 = false,
            ExportTicks = false
        };

        // If the user didn't pass --format, default to csv+hst (full bar export).
        // If they did pass --format none, that's a no-op run — preserved for now;
        // Layer 2 may add validation.
        if (!argMap.ContainsKey("format"))
        {
            options = options with { OutputFormat = OutputFormat.CsvHst };
        }

        return Program.RunDownloadFlow(options);
    }
}
