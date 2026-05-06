namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache update`. Includes everything that affects what
/// gets downloaded into the .bi5 pool — symbols, dates, mode, refresh
/// policy, checksum and validation flags. Does NOT include rendering or
/// export options (timeframe, format, output path, offset, session
/// calendar) because cache update doesn't write any export artifacts.
///
/// `Instrument` is the single-symbol path (legacy `--instrument`);
/// `Instruments` is the list/all path (`--symbols`). The downstream code
/// resolves them in priority order during the run.
/// </summary>
internal sealed record CacheUpdateOptions(
    string Instrument,
    string Instruments,
    DateTimeOffset Start,
    DateTimeOffset End,
    DownloadMode DownloadMode,
    string PoolPath,
    string HttpConfigPath,
    string InstrumentsConfigPath,
    bool RefreshCache,
    int RecentRefreshDays,
    bool VerifyChecksum,
    bool RepairGaps,
    bool ValidateM1,
    int ValidationTolerancePoints,
    int Digits,
    string DigitsMap,
    bool NonInteractive,
    bool Verbose)
{
    public static CacheUpdateOptions FromArgs(IReadOnlyDictionary<string, string> args)
    {
        var (start, end) = CommonParsingHelpers.ParseStartEnd(args);
        return new CacheUpdateOptions(
            Instrument: CommonParsingHelpers.ParseInstrument(args),
            Instruments: CommonParsingHelpers.ParseInstruments(args),
            Start: start,
            End: end,
            DownloadMode: CommonParsingHelpers.ParseDownloadMode(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            HttpConfigPath: CommonParsingHelpers.ParseHttpConfigPath(args),
            InstrumentsConfigPath: CommonParsingHelpers.ParseInstrumentsConfigPath(args),
            RefreshCache: CommonParsingHelpers.ParseRefreshCache(args),
            RecentRefreshDays: CommonParsingHelpers.ParseRecentRefreshDays(args),
            VerifyChecksum: CommonParsingHelpers.ParseVerifyChecksum(args),
            RepairGaps: CommonParsingHelpers.ParseRepairGaps(args),
            ValidateM1: CommonParsingHelpers.ParseValidateM1(args),
            ValidationTolerancePoints: CommonParsingHelpers.ParseValidationTolerancePoints(args),
            Digits: CommonParsingHelpers.ParseDigits(args),
            DigitsMap: CommonParsingHelpers.ParseDigitsMap(args),
            NonInteractive: CommonParsingHelpers.ParseNonInteractive(args),
            Verbose: CommonParsingHelpers.ParseVerbose(args));
    }
}
