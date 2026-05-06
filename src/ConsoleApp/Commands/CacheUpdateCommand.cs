using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache update` — fill or extend the .bi5 data pool from Dukascopy.
/// Downloads only; no CSV/HST/tick CSV outputs are produced.
///
/// Honours the same refresh-policy flags as the legacy CLI: by default
/// every requested hour is fetched fresh; pass `--no-refresh` to use
/// the cache for files older than `--recent-refresh-days` (default 30).
/// Useful as a daily/weekly maintenance pass.
/// </summary>
public sealed class CacheUpdateCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheUpdateOptions.FromArgs(argMap);
        return Program.RunDownloadFlow(ToAppOptions(options));
    }

    /// <summary>
    /// Bridge to the legacy engine: project CacheUpdateOptions onto AppOptions
    /// and apply the cache-update invariants (no bar exports, no tick CSV).
    /// Layer 3 will replace <see cref="Program.RunDownloadFlow"/> with a
    /// dedicated cache-update entry point that takes CacheUpdateOptions
    /// directly, removing the need for this conversion.
    /// </summary>
    internal static AppOptions ToAppOptions(CacheUpdateOptions options)
    {
        return AppOptions.Defaults with
        {
            Instrument = options.Instrument,
            Instruments = options.Instruments,
            Start = options.Start,
            End = options.End,
            DownloadMode = options.DownloadMode,
            DataPoolPath = options.PoolPath,
            HttpConfigPath = options.HttpConfigPath,
            InstrumentsPath = options.InstrumentsConfigPath,
            RefreshCache = options.RefreshCache,
            RecentRefreshDays = options.RecentRefreshDays,
            VerifyChecksum = options.VerifyChecksum,
            RepairGaps = options.RepairGaps,
            ValidateM1 = options.ValidateM1,
            ValidationTolerancePoints = options.ValidationTolerancePoints,
            Digits = options.Digits,
            DigitsMap = options.DigitsMap,
            NonInteractive = options.NonInteractive,
            Verbose = options.Verbose,
            // Cache-update invariants — no exports produced.
            OutputFormat = OutputFormat.None,
            ExportTicks = false
        };
    }
}
