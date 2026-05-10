namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache verify`. Walks the pool, recomputes SHA-256 on
/// every cached .bi5 file, compares against the sidecar .meta.json (no
/// network). With <see cref="Remote"/>, additionally probes Dukascopy for
/// each locally-clean file to detect drift between the cache and the
/// source — catches amended ticks, copy-from-elsewhere mismatches, and
/// other silent source-side changes.
///
/// <see cref="SizeOnly"/> matters only when <see cref="Remote"/> is also
/// set: it falls back to a Content-Length compare (no body download)
/// instead of the default byte-exact hash. Faster, less safe.
///
/// <see cref="Parallel"/> controls the concurrency of remote probes.
/// Default 4 (matches <c>cache discover</c>). Sequential probing is
/// impractical for full-pool drift checks — a 50k-file symbol takes
/// hours at parallel=1 — so the default is the right balance for
/// typical Dukascopy + home-connection conditions.
///
/// <see cref="InstrumentFilter"/> is null when no filter was passed —
/// meaning verify every symbol in the pool. Otherwise it's the explicit
/// list from <c>--instrument</c> or <c>--symbols</c>.
/// </summary>
internal sealed record CacheVerifyOptions(
    IReadOnlyCollection<string>? InstrumentFilter,
    string PoolPath,
    bool Remote,
    bool SizeOnly,
    int Parallel,
    DateTimeOffset? StartUtc,
    DateTimeOffset? EndUtc,
    bool Quiet)
{
    public static CacheVerifyOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            InstrumentFilter: CommonParsingHelpers.ParseInstrumentFilter(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            Remote: CommonParsingHelpers.ParseRemote(args),
            SizeOnly: CommonParsingHelpers.ParseSizeOnly(args),
            // Verify probes every cached file (tens of thousands per symbol on
            // a full history), so 8 concurrent probes is the sensible default
            // — vs cache discover's 4, which only does a per-symbol binary
            // search. Both stay well within Dukascopy's tolerated concurrency.
            Parallel: CommonParsingHelpers.ParseParallel(args, defaultValue: HistoricalData.Manage.CacheVerifier.DefaultRemoteParallelism),
            StartUtc: CommonParsingHelpers.ParseStartUtcOptional(args),
            EndUtc: CommonParsingHelpers.ParseEndUtcOptional(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
}
