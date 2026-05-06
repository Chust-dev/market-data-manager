using HistoricalData.Commands.Options;
using HistoricalData.Export;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `export bars` — read the .bi5 pool (no network) and write MT5-compatible
/// bar files (CSV and/or HST) for the requested instrument and timeframe.
///
/// Thin wrapper over the Export pillar's <see cref="BarExporter"/>. The
/// command's job is just to parse args into <see cref="BarExportOptions"/>
/// and hand them off; the pillar class does the real work.
/// </summary>
public sealed class ExportBarsCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = BarExportOptions.FromArgs(argMap);
        return new BarExporter().RunAsync(options);
    }
}
