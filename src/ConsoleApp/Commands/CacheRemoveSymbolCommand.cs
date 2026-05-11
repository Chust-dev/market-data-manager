using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache remove-symbol` — drop a symbol from all three sections of
/// <c>instruments.json</c>: <c>digits</c>, <c>earliest</c>, <c>latest</c>.
/// Prompts for confirmation before saving unless <c>--no-prompt</c> is
/// passed.
///
/// Does NOT delete cached <c>.bi5</c> files on disk — the command output
/// points the user at <c>cache cleanup --instrument SYM</c> for that, so
/// you can decide separately whether to reclaim the disk space.
///
/// Example usage:
///   cache remove-symbol --instrument BTCUSD
///   cache remove-symbol --instrument BTCUSD --no-prompt
/// </summary>
public sealed class CacheRemoveSymbolCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheRemoveSymbolOptions.FromArgs(argMap);
        return Task.FromResult(Program.RunRemoveSymbol(options));
    }
}
