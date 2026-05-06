using HistoricalData.Commands.Options;
using HistoricalData.Download;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache update` — fill or extend the .bi5 data pool from Dukascopy.
/// Downloads only; no CSV/HST/tick CSV outputs are produced.
///
/// Thin wrapper over the Download pillar's <see cref="Downloader"/>. The
/// command's job is just to parse args into <see cref="CacheUpdateOptions"/>
/// and hand them off; the pillar class does the real work.
/// </summary>
public sealed class CacheUpdateCommand : ICommand
{
    public Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheUpdateOptions.FromArgs(argMap);
        return new Downloader().RunAsync(options);
    }
}
