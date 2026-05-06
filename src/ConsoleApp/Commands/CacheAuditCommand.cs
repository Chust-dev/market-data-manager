using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache audit` — fast directory-level inspection of the data pool.
/// Walks the cache, counts files per symbol, computes coverage rate,
/// flags zero-byte files, reports disk usage. No network calls.
///
/// Filters: `--instrument SYMBOL` to focus on one pair, or
/// `--symbols SYMBOL1,SYMBOL2,...` for a subset. With no filter,
/// every symbol cached under the pool is reported.
/// </summary>
public sealed class CacheAuditCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheAuditOptions.FromArgs(argMap);
        return Task.FromResult(Program.RunAudit(options));
    }
}
