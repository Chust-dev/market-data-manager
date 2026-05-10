using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache verify` — offline checksum verification of cached .bi5 files
/// against their sidecar metadata. With <c>--remote</c>, additionally
/// probes Dukascopy to detect drift between the cache and the source.
///
/// Local check (default): walks the pool, recomputes SHA-256 on every
/// file, compares against the sidecar <c>.meta.json</c>. Reports
/// Ok / NoMetadata / SizeMismatch / HashMismatch / IoError.
///
/// Remote check (<c>--remote</c>): for each locally-clean file, fetches
/// from Dukascopy and compares. <b>Default is byte-exact</b> (downloads
/// each file body, computes SHA-256 on the fly). Pass <c>--size-only</c>
/// to fall back to Content-Length compare without body download — faster
/// but won't catch same-length content changes.
///
/// Exit code: 0 if every cached file passes both local and (if performed)
/// remote checks, 1 otherwise. Honours <c>--instrument</c> / <c>--symbols</c>
/// filters and <c>--quiet</c>; Ctrl+C cancels mid-run.
/// </summary>
public sealed class CacheVerifyCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheVerifyOptions.FromArgs(argMap);
        return Program.RunVerifyAsync(options);
    }
}
