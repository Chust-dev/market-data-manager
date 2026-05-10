namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache audit`. Intentionally minimal — the audit only
/// needs a pool path and an optional symbol filter for the basic summary.
/// The two coverage-grid flags (<see cref="ByYear"/>, <see cref="ByMonth"/>)
/// are opt-in, with one piece of smart-default behaviour: when the audit
/// is scoped to a single symbol, the month grid is included automatically
/// unless <see cref="NoByMonth"/> is set (the output is bounded so the
/// extra detail is almost always wanted).
///
/// `InstrumentFilter` is null when no filter was passed — meaning audit
/// every symbol present in the pool. Otherwise it's the explicit list
/// the user requested via `--instrument` or `--symbols`.
/// </summary>
internal sealed record CacheAuditOptions(
    IReadOnlyCollection<string>? InstrumentFilter,
    string PoolPath,
    bool ByYear,
    bool ByMonth,
    bool NoByMonth)
{
    public static CacheAuditOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            InstrumentFilter: CommonParsingHelpers.ParseInstrumentFilter(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            ByYear: CommonParsingHelpers.ParseByYear(args),
            ByMonth: CommonParsingHelpers.ParseByMonth(args),
            NoByMonth: CommonParsingHelpers.ParseNoByMonth(args));

    /// <summary>
    /// Effective by-month flag, after applying the single-symbol auto-include.
    /// Explicit `--by-month` always wins; explicit `--no-by-month` always
    /// suppresses. Otherwise: include the grid when exactly one symbol is in
    /// scope.
    /// </summary>
    public bool EffectiveByMonth =>
        ByMonth || (!NoByMonth && InstrumentFilter is { Count: 1 });
}
