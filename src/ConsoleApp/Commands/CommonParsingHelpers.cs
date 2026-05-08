using System.Globalization;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// Shared parsing helpers used by per-command options builders. Each helper
/// reads from an argMap and returns a typed value, with defaults that match
/// the legacy <see cref="AppOptions.Defaults"/> so the new and old CLI
/// surfaces stay in lockstep until Layer 3 removes AppOptions.
///
/// All helpers are pure — no side effects, no shared state, safe to call
/// in any order.
/// </summary>
internal static class CommonParsingHelpers
{
    // Pull defaults from the existing AppOptions until Layer 3 inlines them
    // here. Centralising in one record lets us tweak both CLI surfaces in lockstep.
    private static AppOptions D => AppOptions.Defaults;

    // -- Single-instrument / symbol-list / filter parsers ---------------------

    public static string ParseInstrument(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("instrument", D.Instrument);

    public static string ParseInstruments(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("symbols", D.Instruments);

    /// <summary>
    /// Audit-style filter: returns null = no filter (every symbol),
    /// or a list of symbols (uppercased) when --symbols / --instrument was passed.
    /// `--symbols all` is treated as no filter.
    /// </summary>
    public static IReadOnlyCollection<string>? ParseInstrumentFilter(IReadOnlyDictionary<string, string> args)
    {
        var symbols = args.GetValueOrDefault("symbols");
        if (!string.IsNullOrWhiteSpace(symbols) && !symbols.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return symbols
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .ToArray();
        }

        // Only treat --instrument as a filter when explicitly passed (not the default fallback).
        if (args.ContainsKey("instrument"))
        {
            var instrument = args.GetValueOrDefault("instrument");
            if (!string.IsNullOrWhiteSpace(instrument))
            {
                return new[] { instrument.ToUpperInvariant() };
            }
        }

        return null;
    }

    // -- Date / time parsers --------------------------------------------------

    public static (DateTimeOffset Start, DateTimeOffset End) ParseStartEnd(IReadOnlyDictionary<string, string> args)
    {
        var start = DateTimeParser.TryParse(args.GetValueOrDefault("start"), D.Start);
        var end = DateTimeParser.TryParse(args.GetValueOrDefault("end"), D.End);
        return (start, end);
    }

    public static TimeSpan ParseUtcOffset(IReadOnlyDictionary<string, string> args) =>
        TimeSpanParser.TryParse(args.GetValueOrDefault("offset"), D.UtcOffset);

    // -- Path parsers ---------------------------------------------------------

    public static string ParsePoolPath(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("pool", D.DataPoolPath);

    public static string ParseOutputPath(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("output", D.OutputPath);

    public static string ParseHttpConfigPath(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("http", D.HttpConfigPath);

    public static string ParseInstrumentsConfigPath(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("instruments", D.InstrumentsPath);

    public static string ParseSessionConfigPath(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("session-config", D.SessionConfigPath);

    // -- Digits parsers -------------------------------------------------------

    public static int ParseDigits(IReadOnlyDictionary<string, string> args)
    {
        var value = args.GetValueOrDefault("digits");
        if (value is null)
        {
            return D.Digits;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : D.Digits;
    }

    public static string ParseDigitsMap(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("digits-map", D.DigitsMap);

    // -- Behaviour flag parsers (booleans that map a CLI flag to options) -----

    public static bool ParseNonInteractive(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("no-prompt");

    /// <summary>
    /// Verbose mode is OPT-IN — pass --verbose to enable the per-URL "Downloading X"
    /// trace output. Default is false: progress bars handle the user-visible feedback
    /// without URL spam interleaving them.
    /// </summary>
    public static bool ParseVerbose(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("verbose");

    /// <summary>
    /// Quiet mode silences all output (progress bars, banners, URL spam, summaries).
    /// Independent from --verbose: --quiet wins if both are present.
    /// Useful for scheduled tasks that just want exit codes.
    /// </summary>
    public static bool ParseQuiet(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("quiet");

    public static bool ParseRefreshCache(IReadOnlyDictionary<string, string> args) =>
        !args.ContainsKey("no-refresh");

    public static bool ParseVerifyChecksum(IReadOnlyDictionary<string, string> args) =>
        !args.ContainsKey("no-verify-checksum");

    public static bool ParseDeduplicateTicks(IReadOnlyDictionary<string, string> args) =>
        !args.ContainsKey("no-dedupe");

    public static bool ParseSkipFallbackIfTicked(IReadOnlyDictionary<string, string> args) =>
        !args.ContainsKey("allow-fallback-overlap");

    public static bool ParseRepairGaps(IReadOnlyDictionary<string, string> args) =>
        !args.ContainsKey("no-repair-gaps");

    public static bool ParseValidateM1(IReadOnlyDictionary<string, string> args) =>
        !args.ContainsKey("no-validate-m1");

    public static bool ParseUseSessionCalendar(IReadOnlyDictionary<string, string> args)
    {
        if (args.ContainsKey("no-session-calendar")) return false;
        if (args.ContainsKey("use-session-calendar")) return true;
        return D.UseSessionCalendar;
    }

    // -- Numeric parsers ------------------------------------------------------

    public static int ParseRecentRefreshDays(IReadOnlyDictionary<string, string> args)
    {
        var value = args.GetValueOrDefault("recent-refresh-days");
        if (value is null)
        {
            return D.RecentRefreshDays;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : D.RecentRefreshDays;
    }

    /// <summary>
    /// Catchup window in days, used by `cache catchup`. Default 60 ("the last
    /// two months"). Negative or unparseable values fall back to default.
    /// </summary>
    public static int ParseWindow(IReadOnlyDictionary<string, string> args)
    {
        const int DefaultWindow = 60;
        var value = args.GetValueOrDefault("window");
        if (value is null)
        {
            return DefaultWindow;
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return DefaultWindow;
        }
        return parsed;
    }

    /// <summary>
    /// Lower bound for `cache discover`'s binary search, in UTC. Default
    /// 2000-01-01 — earlier than any Dukascopy data we've ever seen
    /// (FX majors typically start ~2003, crypto ~2017). Override with
    /// `--since YYYY-MM-DD` to bound the search tighter when you know
    /// the symbol is recent.
    /// </summary>
    public static DateTimeOffset ParseSince(IReadOnlyDictionary<string, string> args)
    {
        var defaultSince = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return DateTimeParser.TryParse(args.GetValueOrDefault("since"), defaultSince);
    }

    /// <summary>
    /// Per-symbol parallelism for `cache discover`. Default 4 — matches
    /// Dukascopy's tolerated concurrency without spawning more requests
    /// than a typical home connection can sustain. Negative or
    /// unparseable values fall back to default.
    /// </summary>
    public static int ParseParallel(IReadOnlyDictionary<string, string> args)
    {
        const int DefaultParallel = 4;
        var value = args.GetValueOrDefault("parallel");
        if (value is null)
        {
            return DefaultParallel;
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return DefaultParallel;
        }
        return parsed;
    }

    /// <summary>
    /// `--refresh` for `cache discover`. By default discover skips
    /// symbols that already have an entry in the discovery section of
    /// instruments.json (idempotent batch runs). With `--refresh`, every
    /// requested symbol is re-probed — useful for new listings.
    /// </summary>
    public static bool ParseRefreshDiscovery(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("refresh");

    public static int ParseValidationTolerancePoints(IReadOnlyDictionary<string, string> args)
    {
        var value = args.GetValueOrDefault("validation-tolerance-points");
        if (value is null)
        {
            return D.ValidationTolerancePoints;
        }
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : D.ValidationTolerancePoints;
    }

    // -- Enum-style parsers ---------------------------------------------------

    public static DownloadMode ParseDownloadMode(IReadOnlyDictionary<string, string> args)
    {
        var mode = args.GetValueOrDefault("mode");
        if (mode is null) return D.DownloadMode;
        if (mode.Equals("direct", StringComparison.OrdinalIgnoreCase)) return DownloadMode.DirectM1;
        if (mode.Equals("ticks", StringComparison.OrdinalIgnoreCase)) return DownloadMode.TickToM1;
        return D.DownloadMode;
    }

    public static OutputFormat ParseOutputFormat(IReadOnlyDictionary<string, string> args)
    {
        var format = args.GetValueOrDefault("format");
        return format?.ToLowerInvariant() switch
        {
            "csv" => OutputFormat.CsvOnly,
            "csv+hst" => OutputFormat.CsvHst,
            "none" => OutputFormat.None,
            _ => D.OutputFormat
        };
    }

    public static string ParseTimeframe(IReadOnlyDictionary<string, string> args) =>
        args.GetValueOrDefault("timeframe", D.Timeframe);
}
