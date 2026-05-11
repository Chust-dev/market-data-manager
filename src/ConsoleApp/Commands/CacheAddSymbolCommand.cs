using HistoricalData.Commands.Options;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache add-symbol` — register a new symbol in <c>instruments.json</c>'s
/// digits map. By default probes Dukascopy first to confirm the symbol
/// exists at source (catches typos like <c>EURUS</c>); pass
/// <c>--no-verify-source</c> to skip the network check. Refuses to
/// overwrite an existing entry unless <c>--force</c> is set.
///
/// Example usage:
///   cache add-symbol --instrument EURGBP --digits 5
///   cache add-symbol --instrument BTCUSD --digits 2 --no-verify-source
///   cache add-symbol --instrument EURUSD --digits 4 --force   # fix wrong digits
/// </summary>
public sealed class CacheAddSymbolCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheAddSymbolOptions.FromArgs(argMap);
        return Program.RunAddSymbolAsync(options);
    }
}
