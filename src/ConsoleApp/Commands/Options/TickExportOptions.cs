using HistoricalData.Export;

namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `export ticks`. The smallest of the export options
/// — tick export only cares about which symbols, what date range, what
/// offset, and where to write. No timeframe (always tick-by-tick),
/// no format selection (always tick CSV in MT5 import format), no
/// aggregator settings (the export pass walks .bi5 files directly,
/// bypassing the bar aggregator).
/// </summary>
internal sealed record TickExportOptions(
    string Instrument,
    string Instruments,
    DateTimeOffset Start,
    DateTimeOffset End,
    BrokerOffset Offset,
    string PoolPath,
    string OutputPath,
    string HttpConfigPath,
    string InstrumentsConfigPath,
    int Digits,
    string DigitsMap,
    bool NonInteractive,
    bool Verbose,
    bool Quiet)
{
    public static TickExportOptions FromArgs(IReadOnlyDictionary<string, string> args)
    {
        var (start, end) = CommonParsingHelpers.ParseStartEnd(args);
        return new TickExportOptions(
            Instrument: CommonParsingHelpers.ParseInstrument(args),
            Instruments: CommonParsingHelpers.ParseInstruments(args),
            Start: start,
            End: end,
            Offset: CommonParsingHelpers.ParseBrokerOffset(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            OutputPath: CommonParsingHelpers.ParseOutputPath(args),
            HttpConfigPath: CommonParsingHelpers.ParseHttpConfigPath(args),
            InstrumentsConfigPath: CommonParsingHelpers.ParseInstrumentsConfigPath(args),
            Digits: CommonParsingHelpers.ParseDigits(args),
            DigitsMap: CommonParsingHelpers.ParseDigitsMap(args),
            NonInteractive: CommonParsingHelpers.ParseNonInteractive(args),
            Verbose: CommonParsingHelpers.ParseVerbose(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
    }
}
