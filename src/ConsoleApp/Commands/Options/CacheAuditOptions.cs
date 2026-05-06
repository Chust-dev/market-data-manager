namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache audit`. Intentionally minimal — the audit only
/// needs a pool path and an optional symbol filter. Everything else
/// (refresh policy, timeframes, output paths, etc.) is irrelevant for
/// reading-and-counting cached files.
///
/// `InstrumentFilter` is null when no filter was passed — meaning audit
/// every symbol present in the pool. Otherwise it's the explicit list
/// the user requested via `--instrument` or `--symbols`.
/// </summary>
internal sealed record CacheAuditOptions(
    IReadOnlyCollection<string>? InstrumentFilter,
    string PoolPath)
{
    public static CacheAuditOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            InstrumentFilter: CommonParsingHelpers.ParseInstrumentFilter(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args));
}
