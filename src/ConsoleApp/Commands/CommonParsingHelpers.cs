using System.Globalization;
using HistoricalData.Export;
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

    /// <summary>
    /// Resolve the UTC → server-local offset for an export command from the
    /// argMap. Accepts either <c>--broker NAME</c> (DST-aware preset) or
    /// <c>--offset +HH:MM</c> (literal fixed offset), but not both. Default
    /// when neither is passed: <c>BrokerOffset.Fixed(TimeSpan.Zero)</c> —
    /// timestamps stay in UTC.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when both flags are present, or when <c>--broker</c> names an
    /// unknown broker (message lists <see cref="BrokerOffset.KnownNames"/>).
    /// </exception>
    public static BrokerOffset ParseBrokerOffset(IReadOnlyDictionary<string, string> args)
    {
        var hasBroker = args.TryGetValue("broker", out var brokerName) && !string.IsNullOrWhiteSpace(brokerName);
        var hasOffset = args.TryGetValue("offset", out var offsetRaw) && !string.IsNullOrWhiteSpace(offsetRaw);

        if (hasBroker && hasOffset)
        {
            throw new ArgumentException("--broker and --offset are mutually exclusive; pick one.");
        }

        if (hasBroker)
        {
            var resolved = BrokerOffset.TryResolve(brokerName!);
            if (resolved is null)
            {
                var known = string.Join(", ", BrokerOffset.KnownNames);
                throw new ArgumentException($"Unknown broker '{brokerName}'. Known brokers: {known}.");
            }
            return resolved;
        }

        return BrokerOffset.Fixed(TimeSpanParser.TryParse(offsetRaw, D.UtcOffset));
    }

    /// <summary>
    /// Resolve the spread-aggregation method for an export command from the
    /// argMap. Accepts <c>--spread-method last|min|mean|median</c>
    /// (case-insensitive). Default <see cref="SpreadMethod.Last"/> keeps
    /// existing M1 exports byte-identical and gives last-sample semantics
    /// at the resampler. Throws on an unrecognised value.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <c>--spread-method</c> is present but doesn't match a
    /// known method (message lists the supported set).
    /// </exception>
    public static SpreadMethod ParseSpreadMethod(IReadOnlyDictionary<string, string> args)
    {
        if (!args.TryGetValue("spread-method", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return SpreadMethod.Last;
        }
        return raw.Trim().ToLowerInvariant() switch
        {
            "last" => SpreadMethod.Last,
            "min" => SpreadMethod.Min,
            "mean" => SpreadMethod.Mean,
            "median" => SpreadMethod.Median,
            _ => throw new ArgumentException(
                $"Unknown --spread-method '{raw}'. Supported: last, min, mean, median."),
        };
    }

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

    /// <summary>
    /// `--dry-run` for `cache repair` (and any future destructive command).
    /// When set, the command reports what it WOULD do without making changes.
    /// </summary>
    public static bool ParseDryRun(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("dry-run");

    /// <summary>
    /// `--trust-existing` for `cache repair`. Inverts the default for files
    /// flagged <c>NoMetadata</c>: instead of refetching from Dukascopy, trust
    /// the local bytes and just regenerate the missing <c>.meta.json</c>
    /// sidecar from a fresh hash. Fast (no network) but doesn't help if the
    /// local file is silently corrupt.
    /// </summary>
    public static bool ParseTrustExisting(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("trust-existing");

    /// <summary>
    /// `--force` for `cache add-symbol`: overwrite an existing digits entry
    /// rather than refusing the add. Off by default to protect curated
    /// entries from accidental clobber.
    /// </summary>
    public static bool ParseForce(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("force");

    /// <summary>
    /// `--no-verify-source` for `cache add-symbol`: skip the Dukascopy
    /// reachability probe and trust the user. Default is to verify so
    /// typos like `EURUS` don't get added.
    /// </summary>
    public static bool ParseNoVerifySource(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("no-verify-source");

    /// <summary>
    /// `--purge` for `cache cleanup`: wipe a symbol's entire pool
    /// subdirectory instead of running the regular junk-file cleanup.
    /// Requires <c>--instrument SYM</c> (or `--symbols A,B,...`). Pairs
    /// with <c>cache remove-symbol</c> to fully retire a symbol's
    /// footprint.
    /// </summary>
    public static bool ParsePurge(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("purge");

    /// <summary>
    /// `--remote` for `cache verify`. Adds a per-file probe to Dukascopy to
    /// detect drift between cached files and source bytes (e.g. amended ticks).
    /// </summary>
    public static bool ParseRemote(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("remote");

    /// <summary>
    /// `--size-only` for `cache verify --remote`. Skips byte-exact hashing and
    /// only compares Content-Length against local file size. Faster (no body
    /// download) but won't catch same-length content changes. Default is
    /// byte-exact (download + SHA-256), per the user-chosen safety.
    /// </summary>
    public static bool ParseSizeOnly(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("size-only");

    /// <summary>
    /// `--sort size|symbol|year|files` for `cache size`. Default
    /// <see cref="HistoricalData.Manage.CacheSizeSort.Size"/> — the most
    /// common question ("where's my disk space going?") answered with
    /// largest-first ordering. Unrecognised values fall back to default.
    /// </summary>
    public static HistoricalData.Manage.CacheSizeSort ParseCacheSizeSort(IReadOnlyDictionary<string, string> args)
    {
        var value = args.GetValueOrDefault("sort");
        return value?.ToLowerInvariant() switch
        {
            "symbol" => HistoricalData.Manage.CacheSizeSort.Symbol,
            "year" => HistoricalData.Manage.CacheSizeSort.Year,
            "files" => HistoricalData.Manage.CacheSizeSort.Files,
            "size" => HistoricalData.Manage.CacheSizeSort.Size,
            _ => HistoricalData.Manage.CacheSizeSort.Size,
        };
    }

    /// <summary>
    /// `--start ISO` as a nullable UTC date — null when the flag is absent
    /// or unparseable. Differs from <see cref="ParseStartEnd"/>, which
    /// always returns a value (defaulted from <see cref="AppOptions"/>).
    /// Used by `cache verify` to scope the run to a date range without
    /// requiring both bounds to be present.
    /// </summary>
    public static DateTimeOffset? ParseStartUtcOptional(IReadOnlyDictionary<string, string> args)
    {
        var value = args.GetValueOrDefault("start");
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// `--end ISO` as a nullable UTC date — null when absent or unparseable.
    /// </summary>
    public static DateTimeOffset? ParseEndUtcOptional(IReadOnlyDictionary<string, string> args)
    {
        var value = args.GetValueOrDefault("end");
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// `--by-year` for `cache audit`. Adds a year-level coverage grid (rows
    /// = symbols, columns = years observed) to the audit report.
    /// </summary>
    public static bool ParseByYear(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("by-year");

    /// <summary>
    /// `--by-month` for `cache audit`. Adds a per-symbol year × month
    /// coverage grid. Auto-included when a single-symbol filter is in
    /// effect (the output is bounded), unless the user passes `--no-by-month`.
    /// </summary>
    public static bool ParseByMonth(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("by-month");

    /// <summary>
    /// `--no-by-month` opt-out for the auto-include behaviour. Useful when
    /// scripting a single-symbol audit and only wanting the summary table.
    /// </summary>
    public static bool ParseNoByMonth(IReadOnlyDictionary<string, string> args) =>
        args.ContainsKey("no-by-month");

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
    /// Per-symbol parallelism for any command exposing <c>--parallel N</c>.
    /// Each caller chooses its own default: <c>cache discover</c> uses 4
    /// (its workload is a binary search per symbol — light), <c>cache verify
    /// --remote</c> uses 8 (it fans out per-file probes across the whole
    /// pool — much heavier and benefits from more concurrency). Negative
    /// or unparseable values fall back to <paramref name="defaultValue"/>.
    /// </summary>
    public static int ParseParallel(IReadOnlyDictionary<string, string> args, int defaultValue)
    {
        var value = args.GetValueOrDefault("parallel");
        if (value is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return defaultValue;
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
