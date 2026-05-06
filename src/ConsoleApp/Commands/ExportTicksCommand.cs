using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `export ticks` — read the .bi5 pool (no network) and write per-month
/// tick CSV files in MT5 Symbol Editor import format.
///
/// Cache-projection counterpart to `cache update`. The cache must already
/// be filled; this command only reads from it. Missing hours are silently
/// skipped.
///
/// Output: SYMBOL_ticks_yyyy-MM.csv files in the output folder, one per
/// calendar month. Long-history exports for a single symbol can produce
/// hundreds of files; that's intentional, MT5's Symbol Editor imports a
/// month at a time.
///
/// Args (all optional):
///   --instrument SYMBOL or --symbols SYMBOL1,SYMBOL2,...|all
///   --start ISO8601    --end ISO8601
///   --offset +HH:MM
///   --pool PATH    --output PATH
///   --no-prompt
///   --quiet
/// </summary>
public sealed class ExportTicksCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = AppOptions.FromArgs(argMap);

        // Export-ticks invariants: cache-only (no network), no bar exports,
        // tick CSV pass enabled. The legacy bar pipeline still runs in Layer 1
        // (some wasted aggregation work that Layer 3 will remove); the
        // OutputFormat=None ensures it doesn't produce any bar files.
        options = options with
        {
            DownloadMode = DownloadMode.TickToM1,
            RefreshCache = false,
            RecentRefreshDays = 0,
            VerifyChecksum = false,
            RepairGaps = false,
            ValidateM1 = false,
            OutputFormat = OutputFormat.None,
            ExportTicks = true
        };

        return Program.RunDownloadFlow(options);
    }
}
