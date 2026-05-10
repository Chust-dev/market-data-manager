namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache repair`. Walks the pool, verifies each .bi5,
/// and surgically fixes problems by either refetching from Dukascopy or
/// regenerating the local <c>.meta.json</c> sidecar. Network-touching by
/// default — Refetch is the action chosen for SizeMismatch, HashMismatch,
/// and (unless <see cref="TrustExisting"/> is set) NoMetadata.
///
/// <see cref="DryRun"/> reports what would be done without changing the
/// pool. <see cref="TrustExisting"/> flips the NoMetadata default to
/// regenerate-sidecar, useful for old caches where missing sidecars are
/// expected and re-downloading them all would burn bandwidth for no
/// safety gain.
/// </summary>
internal sealed record CacheRepairOptions(
    IReadOnlyCollection<string>? InstrumentFilter,
    string PoolPath,
    bool DryRun,
    bool TrustExisting,
    bool Quiet)
{
    public static CacheRepairOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            InstrumentFilter: CommonParsingHelpers.ParseInstrumentFilter(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            DryRun: CommonParsingHelpers.ParseDryRun(args),
            TrustExisting: CommonParsingHelpers.ParseTrustExisting(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
}
