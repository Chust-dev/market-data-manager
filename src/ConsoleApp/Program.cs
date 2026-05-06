using HistoricalData.Audit;
using HistoricalData.Commands;
using HistoricalData.Commands.Options;
using HistoricalData.Config;
using HistoricalData.DataPool;
using HistoricalData.Export;
using HistoricalData.Models;
using HistoricalData.Utils;

namespace HistoricalData;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return 0;
        }

        // Subcommand routing: peek at the first token. If it matches a known
        // verb, dispatch to the matching command class. Otherwise fall through
        // to the legacy flat-flag handling below so existing scripts keep
        // working unchanged.
        if (args.Length > 0)
        {
            var dispatch = await TryDispatchSubcommandAsync(args);
            if (dispatch.Handled)
            {
                return dispatch.ExitCode;
            }
        }

        var argMap = ArgParser.Parse(args);
        var options = AppOptions.FromArgs(argMap);

        if (options.Audit)
        {
            return RunAudit(CacheAuditOptions.FromArgs(argMap));
        }

        return await RunDownloadFlow(options);
    }

    /// <summary>
    /// The main download/aggregate/export pipeline. Originally inlined inside
    /// <see cref="Main"/>; extracted in Layer 1 of the refactor so that
    /// subcommand classes (CacheUpdate, ExportBars, ExportTicks) can invoke it
    /// with their own preconfigured <see cref="AppOptions"/>.
    ///
    /// Behaviour unchanged from the inlined version: interactive prompts (if
    /// not <c>NonInteractive</c>), timeframe parse, instrument resolution,
    /// per-instrument download/aggregate/write loop, and a final batch summary.
    /// </summary>
    internal static async Task<int> RunDownloadFlow(AppOptions options)
    {
        var httpConfig = HttpConfig.Load(options.HttpConfigPath);
        var instrumentConfig = InstrumentConfig.Load(options.InstrumentsPath);

        if (!options.NonInteractive)
        {
            var availableInstruments = instrumentConfig.Digits.Keys
                .OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase)
                .ToList();
            options = ConsolePrompts.FillMissing(options, availableInstruments);
        }

        if (!TimeframeUtils.TryParse(options.Timeframe, out var timeframeInfo))
        {
            Console.WriteLine($"Unsupported timeframe. {TimeframeUtils.SupportedTimeframeHint}");
            return 1;
        }

        var requestedInstruments = ResolveRequestedInstruments(options.Instrument, options.Instruments, instrumentConfig).ToList();
        if (requestedInstruments.Count == 0)
        {
            Console.WriteLine("No instruments selected.");
            return 1;
        }

        var poolPath = PathUtils.NormalizePath(options.DataPoolPath);
        var outputPath = PathUtils.NormalizePath(options.OutputPath);
        Directory.CreateDirectory(poolPath);
        Directory.CreateDirectory(outputPath);

        var client = new DukascopyClient(httpConfig, poolPath, options.Verbose);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Cancellation requested. Finishing current operation...");
        };

        var startUtc = options.Start.ToUniversalTime();
        var endUtc = options.End.ToUniversalTime();
        if (endUtc < startUtc)
        {
            Console.WriteLine("End time must be after start time.");
            return 1;
        }

        var timeframeMinutes = timeframeInfo.Minutes;

        SessionConfig.SessionCalendar? sessionCalendar = null;
        if (options.UseSessionCalendar)
        {
            var sessionConfig = SessionConfig.Load(options.SessionConfigPath);
            sessionCalendar = new SessionConfig.SessionCalendar(sessionConfig);
        }

        var succeeded = 0;
        var failed = 0;
        var digitsMapOverrides = ParseDigitsMap(options.DigitsMap);
        foreach (var instrument in requestedInstruments)
        {
            if (cts.IsCancellationRequested)
            {
                break;
            }
            try
            {
                var probe = await client.ProbeSymbolAvailabilityAsync(instrument, startUtc, endUtc, cts.Token);
                if (probe.Status == SymbolProbeStatus.NotFound)
                {
                    Console.WriteLine($"No valid symbol on Dukascopy: '{instrument}'.");
                    failed++;
                    continue;
                }

                if (probe.Status == SymbolProbeStatus.TransientError)
                {
                    Console.WriteLine($"Probe failed for {instrument}: {probe.ErrorMessage ?? "Transient network/API error"}");
                    failed++;
                    continue;
                }

                var digits = await ResolveDigitsAsync(
                    client,
                    instrument,
                    options.Digits,
                    instrumentConfig,
                    digitsMapOverrides,
                    startUtc,
                    endUtc,
                    cts.Token);

                var instrumentOptions = options with { Instrument = instrument };
                var instrumentSummary = await RunInstrumentAsync(
                    client,
                    instrumentOptions,
                    startUtc,
                    endUtc,
                    timeframeInfo,
                    timeframeMinutes,
                    digits,
                    outputPath,
                    sessionCalendar,
                    cts.Token);
                if (instrumentSummary)
                {
                    succeeded++;
                }
                else
                {
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"Canceled while processing {instrument}.");
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed instrument {instrument}: {ex.Message}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine("Batch summary:");
        Console.WriteLine($"  Requested: {requestedInstruments.Count}");
        Console.WriteLine($"  Succeeded: {succeeded}");
        Console.WriteLine($"  Failed:    {failed}");

        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Routes verbs like `cache audit` to the matching <see cref="ICommand"/>.
    /// Returns Handled=false if the args don't begin with a recognised verb,
    /// so the caller can fall through to legacy flat-flag handling.
    /// </summary>
    internal static async Task<(bool Handled, int ExitCode)> TryDispatchSubcommandAsync(string[] args)
    {
        // Verbs are two-token: "cache audit", "export bars", etc. Anything
        // starting with "--" is definitely not a verb.
        if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return (false, 0);
        }

        ICommand? command = ResolveCommand(args);
        if (command is null)
        {
            return (false, 0);
        }

        // Pass the remaining args (after the verb) to the command.
        var commandArgs = args.Skip(2).ToArray();
        var exitCode = await command.RunAsync(commandArgs);
        return (true, exitCode);
    }

    /// <summary>
    /// Maps `args[0..1]` to a command instance, or null if no verb matched.
    /// Exposed as <c>internal</c> so the test suite can assert the verb table
    /// without invoking the (network-touching) <see cref="ICommand.RunAsync"/>.
    /// </summary>
    internal static ICommand? ResolveCommand(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            return null;
        }

        return (args[0].ToLowerInvariant(), args.Length > 1 ? args[1].ToLowerInvariant() : "") switch
        {
            ("cache", "audit") => new CacheAuditCommand(),
            ("cache", "update") => new CacheUpdateCommand(),
            ("export", "bars") => new ExportBarsCommand(),
            ("export", "ticks") => new ExportTicksCommand(),
            _ => null
        };
    }

    internal static int RunAudit(CacheAuditOptions options)
    {
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        var auditor = new PoolAuditor(poolPath);
        var report = auditor.Audit(options.InstrumentFilter);
        Console.Write(report.Render());
        return report.PoolExists ? 0 : 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Dukascopy Historical Tick Downloader");
        Console.WriteLine();
        Console.WriteLine("Subcommand usage (preferred):");
        Console.WriteLine("  cache update   --instrument SYM --start ISO --end ISO [--mode ticks|direct]");
        Console.WriteLine("                 Fill or extend the .bi5 cache. No exports written.");
        Console.WriteLine("  cache audit    [--instrument SYM]");
        Console.WriteLine("                 Inspect the cache: file counts, coverage, disk usage.");
        Console.WriteLine("  export bars    --instrument SYM --start ISO --end ISO --timeframe TF [--format csv|csv+hst]");
        Console.WriteLine("                 Read cache (offline), write MT5 bar CSV/HST.");
        Console.WriteLine("  export ticks   --instrument SYM --start ISO --end ISO");
        Console.WriteLine("                 Read cache (offline), write per-month tick CSVs (MT5 import format).");
        Console.WriteLine();
        Console.WriteLine("Legacy flat-flag invocation (still supported):");
        Console.WriteLine("  --instrument EURUSD");
        Console.WriteLine("  --symbols EURUSD,XAUUSD|all");
        Console.WriteLine("  --digits 5");
        Console.WriteLine("  --digits-map EURUSD=5,XAUUSD=3");
        Console.WriteLine("  --start 2025-01-01T00:00:00Z");
        Console.WriteLine("  --end 2025-01-03T00:00:00Z");
        Console.WriteLine("  --timeframe m1|m5|m15|m30|h1|h4|h6|d1|w1|mn1|m<minutes>");
        Console.WriteLine("  --mode direct|ticks");
        Console.WriteLine("  --format csv|csv+hst");
        Console.WriteLine("  --offset +02:00");
        Console.WriteLine("  --pool /DataPool");
        Console.WriteLine("  --output ./output");
        Console.WriteLine("  --instruments ./src/ConsoleApp/Config/instruments.json");
        Console.WriteLine("  --http ./src/ConsoleApp/Config/http.json");
        Console.WriteLine("  --no-refresh");
        Console.WriteLine("  --recent-refresh-days 30");
        Console.WriteLine("  --verify-checksum");
        Console.WriteLine("  --no-verify-checksum");
        Console.WriteLine("  --no-dedupe");
        Console.WriteLine("  --skip-fallback-overlap");
        Console.WriteLine("  --allow-fallback-overlap");
        Console.WriteLine("  --repair-gaps");
        Console.WriteLine("  --no-repair-gaps");
        Console.WriteLine("  --validate-m1");
        Console.WriteLine("  --no-validate-m1");
        Console.WriteLine("  --validation-tolerance-points 1");
        Console.WriteLine("  --use-session-calendar");
        Console.WriteLine("  --no-session-calendar");
        Console.WriteLine("  --session-config ./src/ConsoleApp/Config/sessions.json");
        Console.WriteLine("  --export-ticks                  Also write per-month tick CSVs (MT5 import format)");
        Console.WriteLine("  --audit                         Inspect the data pool and print a coverage summary; no download");
        Console.WriteLine("  --no-prompt");
        Console.WriteLine("  --quiet");
    }

    private static bool IsWithinTolerance(Bar left, Bar right, double tolerance)
    {
        return Math.Abs(left.Open - right.Open) <= tolerance
               && Math.Abs(left.High - right.High) <= tolerance
               && Math.Abs(left.Low - right.Low) <= tolerance
               && Math.Abs(left.Close - right.Close) <= tolerance;
    }

    internal static IEnumerable<string> ResolveRequestedInstruments(string? instrument, string? instruments, InstrumentConfig instrumentConfig)
    {
        var value = instruments?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            if (!string.IsNullOrWhiteSpace(instrument))
            {
                yield return instrument.Trim().ToUpperInvariant();
            }

            yield break;
        }

        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var symbol in instrumentConfig.Digits.Keys.OrderBy(symbol => symbol, StringComparer.OrdinalIgnoreCase))
            {
                yield return symbol;
            }

            yield break;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var symbol = token.ToUpperInvariant();
            if (seen.Add(symbol))
            {
                yield return symbol;
            }
        }
    }

    internal static async Task<int> ResolveDigitsAsync(
        DukascopyClient client,
        string instrument,
        int digitsOverride,
        InstrumentConfig instrumentConfig,
        IReadOnlyDictionary<string, int> digitsMapOverrides,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        if (digitsMapOverrides.TryGetValue(instrument, out var mappedDigits) && mappedDigits > 0)
        {
            Console.WriteLine($"Digits for {instrument}: {mappedDigits} (source: --digits-map)");
            return mappedDigits;
        }

        if (digitsOverride > 0)
        {
            Console.WriteLine($"Digits for {instrument}: {digitsOverride} (source: --digits)");
            return digitsOverride;
        }

        if (instrumentConfig.TryGetDigits(instrument, out var configDigits))
        {
            Console.WriteLine($"Digits for {instrument}: {configDigits} (source: instruments config)");
            return configDigits;
        }

        var detected = await client.TryDetectDigitsAsync(instrument, startUtc, endUtc, cancellationToken);
        if (detected is > 0)
        {
            Console.WriteLine($"Digits for {instrument}: {detected.Value} (source: auto-detect)");
            return detected.Value;
        }

        Console.WriteLine($"Digits for {instrument}: 5 (source: fallback default)");
        return 5;
    }

    internal static IReadOnlyDictionary<string, int> ParseDigitsMap(string value)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (var token in value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = token.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            if (int.TryParse(parts[1], out var digits) && digits > 0)
            {
                map[parts[0].ToUpperInvariant()] = digits;
            }
        }

        return map;
    }

    private static async Task<bool> RunInstrumentAsync(
        DukascopyClient client,
        AppOptions options,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeframeInfo timeframeInfo,
        int timeframeMinutes,
        int digits,
        string outputPath,
        SessionConfig.SessionCalendar? sessionCalendar,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {options.Instrument} ===");

        var summary = new SummaryReport();
        var aggregator = new BarAggregator(
            "m1",
            digits,
            options.UtcOffset,
            options.FilterWeekends,
            startUtc,
            endUtc,
            options.DeduplicateTicks,
            options.SkipFallbackIfTicked,
            sessionCalendar);

        if (options.DownloadMode == DownloadMode.TickToM1)
        {
            await client.DownloadTicksAndAggregate(
                options.Instrument,
                startUtc,
                endUtc,
                digits,
                options.FallbackToM1,
                options.RefreshCache,
                options.VerifyChecksum,
                options.RecentRefreshDays,
                aggregator,
                summary,
                cancellationToken);
        }
        else
        {
            await client.DownloadM1Bars(
                options.Instrument,
                startUtc,
                endUtc,
                digits,
                options.RefreshCache,
                options.VerifyChecksum,
                options.RecentRefreshDays,
                aggregator,
                summary,
                cancellationToken);
        }

        var m1Bars = aggregator.GetBars();
        if (options.RepairGaps && options.DownloadMode == DownloadMode.TickToM1)
        {
            var repairAggregator = new BarAggregator(
                "m1",
                digits,
                options.UtcOffset,
                options.FilterWeekends,
                startUtc,
                endUtc,
                deduplicateTicks: false,
                skipFallbackIfTicked: true,
                sessionCalendar: sessionCalendar);
            var repairSummary = new SummaryReport();
            await client.DownloadM1Bars(
                options.Instrument,
                startUtc,
                endUtc,
                digits,
                options.RefreshCache,
                options.VerifyChecksum,
                options.RecentRefreshDays,
                repairAggregator,
                repairSummary,
                cancellationToken);

            var repairBars = repairAggregator.GetBars();
            var added = 0;
            foreach (var bar in repairBars)
            {
                if (aggregator.TryAddFallbackBar(bar, onlyIfMissing: true))
                {
                    added++;
                }
            }

            summary.GapRepairBarsAdded = added;
            summary.GapRepairBarsSkipped = repairBars.Count - added;
            m1Bars = aggregator.GetBars();
        }

        if (options.ValidateM1 && options.DownloadMode == DownloadMode.TickToM1)
        {
            var validateAggregator = new BarAggregator(
                "m1",
                digits,
                options.UtcOffset,
                options.FilterWeekends,
                startUtc,
                endUtc,
                deduplicateTicks: false,
                skipFallbackIfTicked: false,
                sessionCalendar: sessionCalendar);
            var validateSummary = new SummaryReport();
            await client.DownloadM1Bars(
                options.Instrument,
                startUtc,
                endUtc,
                digits,
                options.RefreshCache,
                options.VerifyChecksum,
                options.RecentRefreshDays,
                validateAggregator,
                validateSummary,
                cancellationToken);

            var validationBars = validateAggregator.GetBars();
            var barMap = m1Bars.ToDictionary(b => b.Time);
            var tolerance = options.ValidationTolerancePoints / Math.Pow(10, digits);
            foreach (var bar in validationBars)
            {
                summary.ValidationChecked++;
                if (!barMap.TryGetValue(bar.Time, out var baseBar) || !IsWithinTolerance(baseBar, bar, tolerance))
                {
                    summary.ValidationMismatches++;
                }
            }
        }

        var bars = m1Bars;
        if (timeframeMinutes > 1)
        {
            bars = BarResampler.Resample(bars, timeframeInfo);
        }

        summary.Bars = bars.Count;
        summary.DuplicateTicksDropped = aggregator.DuplicateTicksDropped;
        summary.FallbackBarsSkipped = aggregator.FallbackBarsSkipped;

        // Bar export step. Skipped entirely when format=None (used by `cache update`,
        // which only fills the pool and shouldn't produce export artifacts).
        string? csvPath = null;
        string? hstPath = null;
        if (options.OutputFormat != OutputFormat.None)
        {
            csvPath = Path.Combine(outputPath, $"{options.Instrument}_{options.Timeframe}.csv");
            CsvWriter.Write(csvPath, bars);

            if (options.OutputFormat == OutputFormat.CsvHst)
            {
                hstPath = Path.Combine(outputPath, $"{options.Instrument}_{options.Timeframe}.hst");
                HstWriter.Write(hstPath, bars, options.Instrument, digits, timeframeMinutes);
            }
        }

        summary.Print();
        if (csvPath is not null)
        {
            Console.WriteLine($"CSV: {csvPath}");
        }
        if (hstPath is not null)
        {
            Console.WriteLine($"HST: {hstPath}");
        }

        if (options.ExportTicks && options.DownloadMode == DownloadMode.TickToM1)
        {
            using var tickWriter = new TickCsvWriter(outputPath, options.Instrument, digits, options.UtcOffset);
            var exportedTicks = await client.ExportTicksToCsvAsync(
                options.Instrument,
                startUtc,
                endUtc,
                digits,
                tickWriter,
                cancellationToken);
            Console.WriteLine($"Tick CSV: {exportedTicks:N0} ticks across {tickWriter.FilesOpened} monthly file(s) in {outputPath}");
        }

        return true;
    }
}
