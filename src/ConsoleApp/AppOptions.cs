namespace HistoricalData;

public enum DownloadMode
{
    TickToM1,
    DirectM1
}

public enum OutputFormat
{
    CsvOnly,
    CsvHst,

    /// <summary>Don't write any bar export files. Used by `cache update`,
    /// which only fills the .bi5 pool and leaves the export step to a
    /// separate `export bars` invocation.</summary>
    None
}

/// <summary>
/// Single-source-of-truth defaults for every option the app exposes via the
/// subcommand CLI. Each <see cref="HistoricalData.Commands.Options"/> record
/// reads its own defaults from <see cref="Defaults"/> via
/// <see cref="HistoricalData.Commands.CommonParsingHelpers"/>, so a default
/// edit here propagates to every subcommand without touching them.
///
/// The legacy flat-flag CLI parser (<c>FromArgs</c>) and interactive prompts
/// were removed in v0.1.0; this type's role narrowed from "god-options record
/// shared by every code path" to "default-value carrier for the typed
/// per-command options records."
/// </summary>
public sealed record AppOptions(
    string Instrument,
    string Instruments,
    int Digits,
    string DigitsMap,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Timeframe,
    DownloadMode DownloadMode,
    OutputFormat OutputFormat,
    TimeSpan UtcOffset,
    string DataPoolPath,
    string OutputPath,
    string InstrumentsPath,
    string HttpConfigPath,
    bool Verbose,
    bool FilterWeekends,
    bool FallbackToM1,
    bool RefreshCache,
    int RecentRefreshDays,
    bool VerifyChecksum,
    bool DeduplicateTicks,
    bool SkipFallbackIfTicked,
    bool RepairGaps,
    bool ValidateM1,
    int ValidationTolerancePoints,
    bool UseSessionCalendar,
    string SessionConfigPath,
    bool NonInteractive,
    bool ExportTicks,
    bool Quiet
)
{
    public static AppOptions Defaults => new(
        Instrument: "EURUSD",
        Instruments: string.Empty,
        Digits: 0,
        DigitsMap: string.Empty,
        Start: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
        End: new DateTimeOffset(2025, 1, 3, 0, 0, 0, TimeSpan.Zero),
        Timeframe: "m1",
        DownloadMode: DownloadMode.TickToM1,
        OutputFormat: OutputFormat.CsvHst,
        UtcOffset: TimeSpan.Zero,
        DataPoolPath: @"D:\MarketData",
        OutputPath: @"D:\MarketData\Exports",
        InstrumentsPath: "./src/ConsoleApp/Config/instruments.json",
        HttpConfigPath: "./src/ConsoleApp/Config/http.json",
        Verbose: false,
        FilterWeekends: true,
        FallbackToM1: true,
        RefreshCache: true,
        RecentRefreshDays: 30,
        VerifyChecksum: true,
        DeduplicateTicks: true,
        SkipFallbackIfTicked: true,
        RepairGaps: true,
        ValidateM1: true,
        ValidationTolerancePoints: 1,
        UseSessionCalendar: false,
        SessionConfigPath: "./src/ConsoleApp/Config/sessions.json",
        NonInteractive: false,
        ExportTicks: false,
        Quiet: false
    );
}
