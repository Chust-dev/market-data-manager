using HistoricalData.Utils;

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
    bool Audit,
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
        Verbose: true,
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
        Audit: false,
        Quiet: false
    );

    public static AppOptions FromArgs(Dictionary<string, string> args)
    {
        var d = Defaults;

        var instrument = args.GetValueOrDefault("instrument", d.Instrument);
        var instruments = args.GetValueOrDefault("symbols", d.Instruments);
        var digits = GetInt(args, "digits", d.Digits);
        var digitsMap = args.GetValueOrDefault("digits-map", d.DigitsMap);
        var timeframe = args.GetValueOrDefault("timeframe", d.Timeframe);
        var dataPoolPath = args.GetValueOrDefault("pool", d.DataPoolPath);
        var outputPath = args.GetValueOrDefault("output", d.OutputPath);
        var instrumentsPath = args.GetValueOrDefault("instruments", d.InstrumentsPath);
        var httpConfigPath = args.GetValueOrDefault("http", d.HttpConfigPath);

        var start = DateTimeParser.TryParse(args.GetValueOrDefault("start"), d.Start);
        var end = DateTimeParser.TryParse(args.GetValueOrDefault("end"), d.End);

        var mode = args.GetValueOrDefault("mode");
        var downloadMode = mode?.Equals("direct", StringComparison.OrdinalIgnoreCase) == true
            ? DownloadMode.DirectM1
            : mode?.Equals("ticks", StringComparison.OrdinalIgnoreCase) == true
                ? DownloadMode.TickToM1
                : d.DownloadMode;

        var format = args.GetValueOrDefault("format");
        var outputFormat = format?.ToLowerInvariant() switch
        {
            "csv" => OutputFormat.CsvOnly,
            "csv+hst" => OutputFormat.CsvHst,
            "none" => OutputFormat.None,
            _ => OutputFormat.CsvHst
        };

        var offset = TimeSpanParser.TryParse(args.GetValueOrDefault("offset"), d.UtcOffset);
        // Verbose is opt-in (--verbose); default is quieter so progress bars
        // can render without URL spam interleaving them.
        var verbose = args.ContainsKey("verbose");
        // Quiet is the kill switch — silences progress bars too, not just URLs.
        var quiet = args.ContainsKey("quiet");
        var nonInteractive = args.ContainsKey("no-prompt");
        var sessionConfigPath = args.GetValueOrDefault("session-config", d.SessionConfigPath);

        var refreshCache = GetBool(args, "refresh", !args.ContainsKey("no-refresh"));
        var recentRefreshDays = GetInt(args, "recent-refresh-days", d.RecentRefreshDays);
        var verifyChecksum = GetBool(args, "verify-checksum", !args.ContainsKey("no-verify-checksum"));
        var deduplicateTicks = GetBool(args, "dedupe", !args.ContainsKey("no-dedupe"));
        var skipFallbackIfTicked = GetBool(args, "skip-fallback-overlap", !args.ContainsKey("allow-fallback-overlap"));
        var repairGaps = GetBool(args, "repair-gaps", !args.ContainsKey("no-repair-gaps"));
        var validateM1 = GetBool(args, "validate-m1", !args.ContainsKey("no-validate-m1"));
        var validationTolerancePoints = GetInt(args, "validation-tolerance-points", d.ValidationTolerancePoints);
        var useSessionCalendar = GetBool(args, "use-session-calendar", d.UseSessionCalendar);
        if (args.ContainsKey("no-session-calendar"))
        {
            useSessionCalendar = false;
        }

        var exportTicks = GetBool(args, "export-ticks", d.ExportTicks);
        var audit = GetBool(args, "audit", d.Audit);

        return d with
        {
            Instrument = instrument,
            Instruments = instruments,
            Digits = digits,
            DigitsMap = digitsMap,
            Start = start,
            End = end,
            Timeframe = timeframe,
            DownloadMode = downloadMode,
            OutputFormat = outputFormat,
            UtcOffset = offset,
            DataPoolPath = dataPoolPath,
            OutputPath = outputPath,
            InstrumentsPath = instrumentsPath,
            HttpConfigPath = httpConfigPath,
            Verbose = verbose,
            RefreshCache = refreshCache,
            RecentRefreshDays = recentRefreshDays,
            VerifyChecksum = verifyChecksum,
            DeduplicateTicks = deduplicateTicks,
            SkipFallbackIfTicked = skipFallbackIfTicked,
            RepairGaps = repairGaps,
            ValidateM1 = validateM1,
            ValidationTolerancePoints = validationTolerancePoints,
            UseSessionCalendar = useSessionCalendar,
            SessionConfigPath = sessionConfigPath,
            NonInteractive = nonInteractive,
            ExportTicks = exportTicks,
            Audit = audit,
            Quiet = quiet
        };
    }

    private static bool GetBool(Dictionary<string, string> args, string key, bool fallback)
    {
        if (!args.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return value.Equals("true", StringComparison.OrdinalIgnoreCase)
               || value.Equals("1", StringComparison.OrdinalIgnoreCase)
               || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || value.Equals("y", StringComparison.OrdinalIgnoreCase)
               || value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetInt(Dictionary<string, string> args, string key, int fallback)
    {
        if (!args.TryGetValue(key, out var value))
        {
            return fallback;
        }

        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}
