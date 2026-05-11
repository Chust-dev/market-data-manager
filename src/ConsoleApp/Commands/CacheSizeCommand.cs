using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache size` — disk-usage breakdown of the .bi5 cache. Per-symbol
/// totals by default, optionally per-(symbol, year) with <c>--by-year</c>.
/// Sortable by size / symbol / year / files. Text-table output today;
/// CSV / file output is a planned follow-up.
///
/// Pairs with <c>cache audit</c> (which shows file counts and coverage)
/// — this is the "where's my disk space going?" view.
/// </summary>
public sealed class CacheSizeCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheSizeOptions.FromArgs(argMap);
        return Task.FromResult(Program.RunSize(options));
    }
}
