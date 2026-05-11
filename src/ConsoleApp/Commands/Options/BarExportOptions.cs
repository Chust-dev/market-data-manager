using HistoricalData.Export;

namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `export bars`. Includes everything that affects the
/// shape of the bar exports — timeframe, format, offset, paths — and the
/// aggregator settings that change how ticks turn into bars (dedup,
/// session-calendar filtering). Does NOT include refresh-policy or
/// download-mode flags: export bars is cache-only by design, those
/// invariants are applied by the command itself.
/// </summary>
internal sealed record BarExportOptions(
    string Instrument,
    string Instruments,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Timeframe,
    OutputFormat OutputFormat,
    TimeSpan UtcOffset,
    string PoolPath,
    string OutputPath,
    string HttpConfigPath,
    string InstrumentsConfigPath,
    bool DeduplicateTicks,
    bool SkipFallbackIfTicked,
    bool UseSessionCalendar,
    string SessionConfigPath,
    int Digits,
    string DigitsMap,
    bool NonInteractive,
    bool Verbose,
    bool Quiet,
    SpreadMethod SpreadMethod)
{
    public static BarExportOptions FromArgs(IReadOnlyDictionary<string, string> args)
    {
        var (start, end) = CommonParsingHelpers.ParseStartEnd(args);
        return new BarExportOptions(
            Instrument: CommonParsingHelpers.ParseInstrument(args),
            Instruments: CommonParsingHelpers.ParseInstruments(args),
            Start: start,
            End: end,
            Timeframe: CommonParsingHelpers.ParseTimeframe(args),
            OutputFormat: CommonParsingHelpers.ParseOutputFormat(args),
            UtcOffset: CommonParsingHelpers.ParseUtcOffset(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            OutputPath: CommonParsingHelpers.ParseOutputPath(args),
            HttpConfigPath: CommonParsingHelpers.ParseHttpConfigPath(args),
            InstrumentsConfigPath: CommonParsingHelpers.ParseInstrumentsConfigPath(args),
            DeduplicateTicks: CommonParsingHelpers.ParseDeduplicateTicks(args),
            SkipFallbackIfTicked: CommonParsingHelpers.ParseSkipFallbackIfTicked(args),
            UseSessionCalendar: CommonParsingHelpers.ParseUseSessionCalendar(args),
            SessionConfigPath: CommonParsingHelpers.ParseSessionConfigPath(args),
            Digits: CommonParsingHelpers.ParseDigits(args),
            DigitsMap: CommonParsingHelpers.ParseDigitsMap(args),
            NonInteractive: CommonParsingHelpers.ParseNonInteractive(args),
            Verbose: CommonParsingHelpers.ParseVerbose(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args),
            SpreadMethod: CommonParsingHelpers.ParseSpreadMethod(args));
    }
}
