using HistoricalData.Manage;

namespace HistoricalData.Download;

/// <summary>
/// Production <see cref="IFileRefetcher"/> backed by <see cref="DukascopyClient"/>.
/// Thin adapter so <see cref="CacheRepairer"/> doesn't take a hard dependency on
/// the HTTP client surface — keeps the Manage pillar test-friendly.
/// </summary>
internal sealed class DukascopyFileRefetcher : IFileRefetcher
{
    private readonly DukascopyClient _client;

    public DukascopyFileRefetcher(DukascopyClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<bool> RefetchAsync(
        string symbol,
        int year,
        int dukaMonth,
        int day,
        string fileName,
        CancellationToken cancellationToken) =>
        _client.RefetchFileAsync(symbol, year, dukaMonth, day, fileName, cancellationToken);
}
