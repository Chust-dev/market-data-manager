namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache add-symbol`. Inserts a new entry into the
/// <c>digits</c> map in <c>instruments.json</c>. By default, probes
/// Dukascopy first to verify the symbol exists at source; set
/// <see cref="NoVerifySource"/> to skip the probe. Refuses to overwrite
/// an existing entry unless <see cref="Force"/> is set.
/// </summary>
internal sealed record CacheAddSymbolOptions(
    string Instrument,
    int Digits,
    bool Force,
    bool NoVerifySource,
    string InstrumentsConfigPath,
    string PoolPath,
    string HttpConfigPath,
    bool Quiet)
{
    public static CacheAddSymbolOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            Instrument: CommonParsingHelpers.ParseInstrument(args),
            Digits: CommonParsingHelpers.ParseDigits(args),
            Force: CommonParsingHelpers.ParseForce(args),
            NoVerifySource: CommonParsingHelpers.ParseNoVerifySource(args),
            InstrumentsConfigPath: CommonParsingHelpers.ParseInstrumentsConfigPath(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            HttpConfigPath: CommonParsingHelpers.ParseHttpConfigPath(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
}
