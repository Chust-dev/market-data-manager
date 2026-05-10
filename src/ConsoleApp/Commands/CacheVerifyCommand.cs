using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache verify` — offline checksum verification of cached .bi5 files.
/// Walks the pool, recomputes SHA-256 on every file, compares against the
/// sidecar .meta.json. Reports OK / no-metadata / size-mismatch /
/// hash-mismatch / I/O-error counts per symbol. No network calls.
///
/// Exit code: 0 if every cached file verifies clean, 1 otherwise (so
/// scheduled tasks can detect drift). Honours `--quiet` to silence the
/// progress bar; `Ctrl+C` cancels mid-run.
///
/// Filters: `--instrument SYMBOL` to focus on one pair, or
/// `--symbols SYMBOL1,SYMBOL2,...` for a subset. With no filter, every
/// symbol cached under the pool is verified.
/// </summary>
public sealed class CacheVerifyCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheVerifyOptions.FromArgs(argMap);
        return Task.FromResult(Program.RunVerify(options));
    }
}
