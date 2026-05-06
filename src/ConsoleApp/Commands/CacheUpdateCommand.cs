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
///
/// Args (all optional):
///   --instrument SYMBOL or --symbols SYMBOL1,SYMBOL2,...|all
///   --start ISO8601    --end ISO8601
///   --mode ticks|direct
///   --offset +HH:MM
///   --pool PATH
///   --no-refresh, --recent-refresh-days N
///   --verify-checksum, --no-verify-checksum
///   --no-validate-m1, --no-repair-gaps
///   --no-prompt
///   --quiet
/// </summary>
public sealed class CacheUpdateCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = AppOptions.FromArgs(argMap);

        // Cache-update invariants: this command's job is the .bi5 pool only.
        // Force off any export-related options the user might have passed.
        options = options with
        {
            OutputFormat = OutputFormat.None,
            ExportTicks = false
        };

        return Program.RunDownloadFlow(options);
    }
}
