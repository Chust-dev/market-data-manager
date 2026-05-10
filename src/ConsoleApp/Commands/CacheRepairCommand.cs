using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache repair` — auto-fixes problems flagged by `cache verify`. For each
/// .bi5 in the pool, inspects the file's checksum status and either
/// re-downloads the file from Dukascopy (default for SizeMismatch /
/// HashMismatch / NoMetadata) or regenerates the local <c>.meta.json</c>
/// sidecar from current bytes (NoMetadata when <c>--trust-existing</c> is
/// set). Files with unrecoverable I/O errors are skipped and reported.
///
/// Filters: <c>--instrument SYMBOL</c> or <c>--symbols SYM1,SYM2,...</c>.
/// <c>--dry-run</c> reports what would happen without touching the cache.
/// <c>--trust-existing</c> flips the NoMetadata default to regenerate-sidecar.
/// <c>--quiet</c> silences the progress bar; Ctrl+C cancels mid-run.
///
/// Exit code: 0 if every problem was resolved (or the pool was already
/// clean), 1 otherwise.
/// </summary>
public sealed class CacheRepairCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheRepairOptions.FromArgs(argMap);
        return Program.RunRepairAsync(options);
    }
}
