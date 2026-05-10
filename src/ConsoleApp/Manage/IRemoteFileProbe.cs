namespace HistoricalData.Manage;

/// <summary>
/// Per-file source-side probe used by <see cref="CacheVerifier"/> to detect
/// drift between a cached <c>.bi5</c> file and Dukascopy's current bytes for
/// the same coordinate. The production implementation
/// (<c>DukascopyRemoteFileProbe</c>) wraps <c>DukascopyClient</c>; tests pass
/// a fake so the verifier can be exercised without network.
///
/// <para>
/// When <c>fetchBody</c> is <c>false</c>, only the size needs to be returned
/// (Content-Length from response headers). When <c>true</c>, the full body
/// must be read and SHA-256 computed on the fly so the verifier can do a
/// byte-exact compare against the local file.
/// </para>
/// </summary>
public interface IRemoteFileProbe
{
    Task<RemoteProbeResult> ProbeAsync(
        string symbol,
        int year,
        int dukaMonth,
        int day,
        string fileName,
        bool fetchBody,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of a single source-side probe.
/// <para>
/// <see cref="ContentLength"/> is non-null on <see cref="RemoteProbeStatus.Ok"/>
/// (and only then). <see cref="Sha256"/> is non-null only when the probe was
/// asked to fetch the body and succeeded.
/// </para>
/// </summary>
public sealed record RemoteProbeResult(
    RemoteProbeStatus Status,
    long? ContentLength,
    string? Sha256);

public enum RemoteProbeStatus
{
    Ok,
    NotFound,     // Dukascopy returned 404 — file is not on the source
    Unreachable,  // network timeout, transient HTTP error, etc.
}
