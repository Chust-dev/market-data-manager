namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache verify`. Walks the pool, recomputes SHA-256 on
/// every cached .bi5 file, compares against the sidecar .meta.json. No
/// network. Honours the same `--quiet` convention as the other Manage-pillar
/// commands so scheduled jobs can run silent and just check the exit code.
///
/// `InstrumentFilter` is null when no filter was passed — meaning verify
/// every symbol in the pool. Otherwise it's the explicit list from
/// `--instrument` or `--symbols`.
/// </summary>
internal sealed record CacheVerifyOptions(
    IReadOnlyCollection<string>? InstrumentFilter,
    string PoolPath,
    bool Quiet)
{
    public static CacheVerifyOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            InstrumentFilter: CommonParsingHelpers.ParseInstrumentFilter(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
}
