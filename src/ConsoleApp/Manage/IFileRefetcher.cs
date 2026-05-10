namespace HistoricalData.Manage;

/// <summary>
/// Per-file refetch primitive used by <see cref="CacheRepairer"/> to replace
/// corrupt or missing cached .bi5 files. The production implementation
/// (<c>DukascopyFileRefetcher</c>) wraps <c>DukascopyClient</c>; tests pass
/// a fake so the orchestration logic can be exercised without network.
///
/// Returns <c>true</c> if the file was successfully fetched and written to
/// the local cache (with a fresh sidecar). <c>false</c> means the source
/// returned 404 or the network repeatedly failed — caller leaves the
/// original local file untouched.
/// </summary>
public interface IFileRefetcher
{
    Task<bool> RefetchAsync(
        string symbol,
        int year,
        int dukaMonth,
        int day,
        string fileName,
        CancellationToken cancellationToken);
}
