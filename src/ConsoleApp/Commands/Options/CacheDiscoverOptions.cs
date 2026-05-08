namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache discover`. The discoverer's job is to find
/// the earliest UTC hour Dukascopy has data for each requested symbol,
/// by binary-searching .bi5 URL availability. Results are merged into
/// the <c>earliest</c> section of instruments.json (or wherever
/// <see cref="OutputPath"/> points).
///
/// `Instrument` (single symbol) and `Instruments` (list / "all") follow
/// the same convention as the Download / Export pillars; see
/// <see cref="HistoricalData.Commands.CacheDiscoverCommand"/> for the
/// resolution order.
/// </summary>
internal sealed record CacheDiscoverOptions(
    string Instrument,
    string Instruments,
    DateTimeOffset Since,
    int Parallel,
    bool RefreshDiscovery,
    string? OutputPath,
    string PoolPath,
    string HttpConfigPath,
    string InstrumentsConfigPath,
    bool NonInteractive,
    bool Verbose,
    bool Quiet)
{
    public static CacheDiscoverOptions FromArgs(IReadOnlyDictionary<string, string> args)
    {
        return new CacheDiscoverOptions(
            Instrument: CommonParsingHelpers.ParseInstrument(args),
            Instruments: CommonParsingHelpers.ParseInstruments(args),
            Since: CommonParsingHelpers.ParseSince(args),
            Parallel: CommonParsingHelpers.ParseParallel(args),
            RefreshDiscovery: CommonParsingHelpers.ParseRefreshDiscovery(args),
            OutputPath: args.TryGetValue("output", out var o) && !string.IsNullOrWhiteSpace(o) ? o : null,
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            HttpConfigPath: CommonParsingHelpers.ParseHttpConfigPath(args),
            InstrumentsConfigPath: CommonParsingHelpers.ParseInstrumentsConfigPath(args),
            NonInteractive: CommonParsingHelpers.ParseNonInteractive(args),
            Verbose: CommonParsingHelpers.ParseVerbose(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
    }
}
