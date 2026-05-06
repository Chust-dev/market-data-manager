using HistoricalData.Commands.Options;
using HistoricalData.Export;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `export ticks` — read the .bi5 pool (no network) and write per-month
/// tick CSV files in MT5 Symbol Editor import format.
///
/// Thin wrapper over the Export pillar's <see cref="TickExporter"/>. The
/// command's job is just to parse args into <see cref="TickExportOptions"/>
/// and hand them off; the pillar class does the real work.
/// </summary>
public sealed class ExportTicksCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = TickExportOptions.FromArgs(argMap);
        return new TickExporter().RunAsync(options);
    }
}
