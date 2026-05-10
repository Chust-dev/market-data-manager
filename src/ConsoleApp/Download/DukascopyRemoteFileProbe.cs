using HistoricalData.Manage;

namespace HistoricalData.Download;

/// <summary>
/// Production <see cref="IRemoteFileProbe"/> backed by <see cref="DukascopyClient"/>.
/// Mirrors the <see cref="DukascopyFileRefetcher"/> pattern: a thin adapter
/// that lets <see cref="CacheVerifier"/> stay decoupled from the HTTP client
/// surface so it can be unit-tested with a fake probe.
/// </summary>
internal sealed class DukascopyRemoteFileProbe : IRemoteFileProbe
{
    private readonly DukascopyClient _client;

    public DukascopyRemoteFileProbe(DukascopyClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public Task<RemoteProbeResult> ProbeAsync(
        string symbol,
        int year,
        int dukaMonth,
        int day,
        string fileName,
        bool fetchBody,
        CancellationToken cancellationToken) =>
        _client.ProbeFileAsync(symbol, year, dukaMonth, day, fileName, fetchBody, cancellationToken);
}
