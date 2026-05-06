using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `export bars` — read the .bi5 pool (no network) and write MT5-compatible
/// bar files (CSV and/or HST) for the requested instrument and timeframe.
///
/// This is the cache-projection counterpart to `cache update`: the cache must
/// already be filled, this command only reads from it. Missing hours are
/// treated as data gaps and silently skipped.
/// </summary>
public sealed class ExportBarsCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = BarExportOptions.FromArgs(argMap);
        return Program.RunDownloadFlow(ToAppOptions(options));
    }

    /// <summary>
    /// Bridge to the legacy engine: project BarExportOptions onto AppOptions
    /// and apply the export-bars invariants (cache-only, no validation/repair,
    /// no tick CSV side-effect, ticks-to-M1 download mode for the aggregator).
    /// Layer 3 will replace this with a direct call to a bar-export entry
    /// point that takes BarExportOptions and a cache reference.
    /// </summary>
    internal static AppOptions ToAppOptions(BarExportOptions options)
    {
        return AppOptions.Defaults with
        {
            Instrument = options.Instrument,
            Instruments = options.Instruments,
            Start = options.Start,
            End = options.End,
            Timeframe = options.Timeframe,
            OutputFormat = options.OutputFormat,
            UtcOffset = options.UtcOffset,
            DataPoolPath = options.PoolPath,
            OutputPath = options.OutputPath,
            HttpConfigPath = options.HttpConfigPath,
            InstrumentsPath = options.InstrumentsConfigPath,
            DeduplicateTicks = options.DeduplicateTicks,
            SkipFallbackIfTicked = options.SkipFallbackIfTicked,
            UseSessionCalendar = options.UseSessionCalendar,
            SessionConfigPath = options.SessionConfigPath,
            Digits = options.Digits,
            DigitsMap = options.DigitsMap,
            NonInteractive = options.NonInteractive,
            Verbose = options.Verbose,
            // Export-bars invariants — cache-only, no side-effects.
            DownloadMode = DownloadMode.TickToM1,
            RefreshCache = false,
            RecentRefreshDays = 0,
            VerifyChecksum = false,
            RepairGaps = false,
            ValidateM1 = false,
            ExportTicks = false
        };
    }
}
