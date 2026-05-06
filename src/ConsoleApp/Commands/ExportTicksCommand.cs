using HistoricalData.Commands.Options;
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
/// </summary>
public sealed class ExportTicksCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = TickExportOptions.FromArgs(argMap);
        return Program.RunDownloadFlow(ToAppOptions(options));
    }

    /// <summary>
    /// Bridge to the legacy engine: project TickExportOptions onto AppOptions
    /// and apply the export-ticks invariants (cache-only, no bar exports,
    /// tick CSV pass enabled). Layer 3 will replace this with a direct call
    /// to a tick-export entry point that takes TickExportOptions.
    /// </summary>
    internal static AppOptions ToAppOptions(TickExportOptions options)
    {
        return AppOptions.Defaults with
        {
            Instrument = options.Instrument,
            Instruments = options.Instruments,
            Start = options.Start,
            End = options.End,
            UtcOffset = options.UtcOffset,
            DataPoolPath = options.PoolPath,
            OutputPath = options.OutputPath,
            HttpConfigPath = options.HttpConfigPath,
            InstrumentsPath = options.InstrumentsConfigPath,
            Digits = options.Digits,
            DigitsMap = options.DigitsMap,
            NonInteractive = options.NonInteractive,
            Verbose = options.Verbose,
            // Export-ticks invariants — cache-only, tick CSV pass on, no bar exports.
            DownloadMode = DownloadMode.TickToM1,
            RefreshCache = false,
            RecentRefreshDays = 0,
            VerifyChecksum = false,
            RepairGaps = false,
            ValidateM1 = false,
            OutputFormat = OutputFormat.None,
            ExportTicks = true
        };
    }
}
