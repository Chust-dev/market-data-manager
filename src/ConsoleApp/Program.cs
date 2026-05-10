using HistoricalData.Manage;
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
        var cancelled = 0;
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
            // Real user cancellation (Ctrl+C) — cts.Token was triggered.
            // We stop the loop and surface a "cancelled" count in the summary.
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Console.WriteLine($"Canceled while processing {instrument}.");
                cancelled++;
                break;
            }
            // Any other exception, including HttpClient TaskCanceledException
            // (request timeout — a subclass of OperationCanceledException but
            // NOT triggered by cts), counts as a per-instrument failure and
            // the loop continues with the next symbol.
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
        if (cancelled > 0)
        {
            Console.WriteLine($"  Cancelled: {cancelled}");
            Console.WriteLine();
            Console.WriteLine("Run cancelled — partial cache committed. Re-run to resume.");
        }

        return failed == 0 && cancelled == 0 ? 0 : 1;
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
            ("cache", "catchup") => new CacheCatchupCommand(),
            ("cache", "discover") => new CacheDiscoverCommand(),
            ("cache", "verify") => new CacheVerifyCommand(),
            ("cache", "repair") => new CacheRepairCommand(),
            ("cache", "cleanup") => new CacheCleanupCommand(),
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

    /// <summary>
    /// Drives the verification flow for `cache verify`. Always runs the local
    /// (sidecar-vs-bytes) check; with <see cref="CacheVerifyOptions.Remote"/>,
    /// additionally probes Dukascopy per locally-clean file to detect drift
    /// against the source. <see cref="CacheVerifyOptions.SizeOnly"/> chooses
    /// the cheap Content-Length probe over the default byte-exact hash.
    /// Returns 0 if both local and (if performed) remote checks are clean,
    /// 1 if any problem is found or the run is cancelled.
    /// </summary>
    internal static async Task<int> RunVerifyAsync(CacheVerifyOptions options)
    {
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        var verifier = new CacheVerifier(poolPath);

        if (!Directory.Exists(poolPath))
        {
            Console.WriteLine($"Pool path does not exist: {poolPath}");
            return 1;
        }

        var plan = verifier.PlanFiles(options.InstrumentFilter);
        if (plan.Count == 0)
        {
            Console.WriteLine($"No .bi5 files to verify in {poolPath}");
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (!options.Quiet)
        {
            if (options.Remote)
            {
                var mode = options.SizeOnly ? "size-only" : "byte-exact";
                Console.WriteLine($"Verifying {plan.Count:N0} cached file(s) in {poolPath} (local + remote drift, {mode})...");
            }
            else
            {
                Console.WriteLine($"Verifying {plan.Count:N0} cached file(s) in {poolPath}...");
            }
        }

        CacheVerifyReport report;
        try
        {
            using var pb = new ProgressBar("Verify", plan.Count, () => verifier.FilesProcessed, options.Quiet);
            if (options.Remote)
            {
                var httpConfig = HttpConfig.Load(AppOptions.Defaults.HttpConfigPath);
                var client = new DukascopyClient(httpConfig, poolPath, verbose: false);
                var probe = new HistoricalData.Download.DukascopyRemoteFileProbe(client);
                report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: !options.SizeOnly, cts.Token);
            }
            else
            {
                report = verifier.RunPlan(plan, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Verification cancelled.");
            return 1;
        }

        Console.WriteLine();
        Console.Write(report.Render());
        return report.AllClean ? 0 : 1;
    }

    /// <summary>
    /// Drives the repair flow for `cache repair`. Composes the Manage pillar's
    /// <see cref="CacheRepairer"/> with a Download-pillar
    /// <see cref="DukascopyFileRefetcher"/> so the orchestrator stays
    /// network-agnostic and unit-testable. Exit codes: 0 if every problem
    /// resolved (or pool was clean), 1 otherwise (problems remain, run
    /// cancelled, or pool path missing).
    /// </summary>
    internal static async Task<int> RunRepairAsync(CacheRepairOptions options)
    {
        var poolPath = PathUtils.NormalizePath(options.PoolPath);

        if (!Directory.Exists(poolPath))
        {
            Console.WriteLine($"Pool path does not exist: {poolPath}");
            return 1;
        }

        // The repair pipeline borrows the existing DukascopyClient — same
        // retry / backoff / base-URL fallback as the bulk downloaders.
        var httpConfig = HttpConfig.Load(AppOptions.Defaults.HttpConfigPath);
        var client = new DukascopyClient(httpConfig, poolPath, verbose: false);
        var refetcher = new HistoricalData.Download.DukascopyFileRefetcher(client);
        var repairer = new CacheRepairer(poolPath, refetcher);

        var plan = repairer.PlanFiles(options.InstrumentFilter);
        if (plan.Count == 0)
        {
            Console.WriteLine($"No .bi5 files found in {poolPath}");
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        if (!options.Quiet)
        {
            var mode = options.DryRun ? "Dry-run" : (options.TrustExisting ? "Repairing (trust-existing)" : "Repairing");
            Console.WriteLine($"{mode}: inspecting {plan.Count:N0} cached file(s) in {poolPath}...");
        }

        CacheRepairReport report;
        try
        {
            using var pb = new ProgressBar("Repair", plan.Count, () => repairer.FilesProcessed, options.Quiet);
            report = await repairer.RunPlanAsync(plan, options.DryRun, options.TrustExisting, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Repair cancelled.");
            return 1;
        }

        Console.WriteLine();
        Console.Write(report.Render());

        if (options.DryRun)
        {
            // Dry-run is informational; not an error if the pool has problems
            // — the user wanted to know what would happen, and now they do.
            return 0;
        }
        return report.ProblemsFound == 0 || report.AllResolved ? 0 : 1;
    }

    /// <summary>
    /// Drives the cleanup flow for `cache cleanup`. Destructive by default —
    /// the user has to pass <c>--dry-run</c> to preview without deleting. We
    /// print a clear warning banner before doing real work so a typo'd
    /// invocation is at least loud about it.
    /// </summary>
    internal static int RunCleanup(CacheCleanupOptions options)
    {
        var poolPath = PathUtils.NormalizePath(options.PoolPath);

        if (!Directory.Exists(poolPath))
        {
            Console.WriteLine($"Pool path does not exist: {poolPath}");
            return 1;
        }

        var cleaner = new CacheCleaner(poolPath);
        var plan = cleaner.PlanCleanup(options.InstrumentFilter);

        if (plan.Count == 0)
        {
            Console.WriteLine($"Nothing to clean up in {poolPath}");
            return 0;
        }

        if (!options.Quiet)
        {
            if (options.DryRun)
            {
                Console.WriteLine($"Dry-run: planning cleanup of {plan.Count:N0} file(s) in {poolPath}...");
            }
            else
            {
                Console.WriteLine($"WARNING: about to DELETE {plan.Count:N0} file(s) from {poolPath}.");
                Console.WriteLine("         (Pass --dry-run to preview without deleting.)");
            }
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        CacheCleanupReport report;
        try
        {
            using var pb = new ProgressBar("Cleanup", plan.Count, () => cleaner.FilesProcessed, options.Quiet);
            report = cleaner.RunPlan(plan, options.DryRun, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("Cleanup cancelled.");
            return 1;
        }

        Console.WriteLine();
        Console.Write(report.Render());
        return report.AnyFailures ? 1 : 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Dukascopy Historical Tick Downloader");
        Console.WriteLine();
        Console.WriteLine("Subcommand usage (preferred):");
        Console.WriteLine("  cache update   --instrument SYM --start ISO --end ISO [--mode ticks|direct]");
        Console.WriteLine("                 Fill or extend the .bi5 cache. No exports written.");
        Console.WriteLine("  cache catchup  [--instrument SYM | --symbols all] [--window 60]");
        Console.WriteLine("                 Refresh the rolling N-day window (default 60 days). For weekly maintenance.");
        Console.WriteLine("  cache discover [--instrument SYM | --symbols all] [--since 2000-01-01] [--parallel 4] [--refresh]");
        Console.WriteLine("                 Find earliest available date per symbol on Dukascopy; record into instruments.json.");
        Console.WriteLine("  cache audit    [--instrument SYM]");
        Console.WriteLine("                 Inspect the cache: file counts, coverage, disk usage.");
        Console.WriteLine("  cache verify   [--instrument SYM] [--remote [--size-only]] [--quiet]");
        Console.WriteLine("                 Recompute SHA-256 vs sidecar (local) and optionally probe Dukascopy for drift.");
        Console.WriteLine("  cache repair   [--instrument SYM] [--dry-run] [--trust-existing] [--quiet]");
        Console.WriteLine("                 Auto-fix files flagged by verify (refetch from Dukascopy or regenerate sidecar).");
        Console.WriteLine("  cache cleanup  [--instrument SYM] [--dry-run] [--quiet]");
        Console.WriteLine("                 DELETES zero-byte .bi5, orphan .meta.json, and .tmp files. Use --dry-run first.");
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
        Console.WriteLine("  --verbose                       Print every download URL (off by default; on, may interleave with the progress bar)");
        Console.WriteLine("  --quiet                         Silent mode: no progress bar, no per-URL trace, no banners");
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
