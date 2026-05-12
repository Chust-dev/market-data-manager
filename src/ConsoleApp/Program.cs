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
        if (args.Length == 0 || args.Any(arg => arg is "--help" or "-h" or "/?"))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        // Subcommand-only CLI surface as of v0.1.0. Any args that don't match a
        // known verb (e.g. `cache audit`, `export bars`) get a clean rejection
        // rather than falling through to the deleted flat-flag handler.
        var dispatch = await TryDispatchSubcommandAsync(args);
        if (dispatch.Handled)
        {
            return dispatch.ExitCode;
        }

        Console.WriteLine($"Unknown command: {string.Join(' ', args)}");
        Console.WriteLine("Run with --help for the list of supported subcommands.");
        return 1;
    }

    /// <summary>
    /// Routes verbs like `cache audit` to the matching <see cref="ICommand"/>.
    /// Returns Handled=false if the args don't begin with a recognised verb,
    /// so <see cref="Main"/> can surface an "unknown command" error.
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
            ("cache", "size") => new CacheSizeCommand(),
            ("cache", "add-symbol") => new CacheAddSymbolCommand(),
            ("cache", "remove-symbol") => new CacheRemoveSymbolCommand(),
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

    /// <summary>
    /// Drives `cache add-symbol`. Validates inputs via
    /// <see cref="InstrumentConfigEditor.TryAdd"/>; by default probes
    /// Dukascopy to confirm the symbol exists at source before mutating
    /// the config. Saves <c>instruments.json</c> only on success.
    /// </summary>
    internal static async Task<int> RunAddSymbolAsync(CacheAddSymbolOptions options)
    {
        var symbol = options.Instrument?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            Console.WriteLine("Pass --instrument SYM. Example: cache add-symbol --instrument EURGBP --digits 5");
            return 1;
        }
        if (options.Digits < 1 || options.Digits > InstrumentConfigEditor.MaxDigits)
        {
            Console.WriteLine($"--digits must be between 1 and {InstrumentConfigEditor.MaxDigits} (got {options.Digits}).");
            return 1;
        }

        var configPath = options.InstrumentsConfigPath;
        var config = InstrumentConfig.Load(configPath);

        // Existing-entry check up-front so we don't burn a network probe
        // when the user just typo'd a symbol they already have. Force flag
        // bypasses.
        if (config.Digits.ContainsKey(symbol) && !options.Force)
        {
            Console.WriteLine($"{symbol} is already in {configPath} with digits={config.Digits[symbol]}. Pass --force to overwrite.");
            return 1;
        }

        // Verify-source probe: a single round-trip to Dukascopy that
        // catches typos before they pollute the config. Skipped with
        // --no-verify-source for exotic symbols Dukascopy doesn't have
        // yet but the user wants tracked.
        if (!options.NoVerifySource)
        {
            if (!options.Quiet)
            {
                Console.WriteLine($"Probing Dukascopy for {symbol}...");
            }
            var httpConfig = HttpConfig.Load(options.HttpConfigPath);
            var poolPath = PathUtils.NormalizePath(options.PoolPath);
            Directory.CreateDirectory(poolPath);
            var client = new DukascopyClient(httpConfig, poolPath, verbose: false);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            // Use a recent fixed week as the probe window — same approach
            // the bulk path uses for symbol availability checks.
            var now = DateTimeOffset.UtcNow;
            var probe = await client.ProbeSymbolAvailabilityAsync(symbol, now.AddDays(-7), now, cts.Token);
            if (probe.Status == SymbolProbeStatus.NotFound)
            {
                Console.WriteLine($"Dukascopy has no data for {symbol}. Refusing to add (use --no-verify-source to skip this check).");
                return 1;
            }
            if (probe.Status == SymbolProbeStatus.TransientError)
            {
                Console.WriteLine(
                    $"Could not reach Dukascopy to verify {symbol}: {probe.ErrorMessage ?? "transient error"}. " +
                    "Refusing to add (use --no-verify-source to skip this check, or retry later).");
                return 1;
            }
        }

        var result = InstrumentConfigEditor.TryAdd(config, symbol, options.Digits, options.Force);
        switch (result)
        {
            case AddSymbolResult.AlreadyExists:
                // Defensive — should be caught by the pre-check above.
                Console.WriteLine($"{symbol} already exists; use --force to overwrite.");
                return 1;
            case AddSymbolResult.InvalidSymbol:
                Console.WriteLine($"Invalid symbol: '{options.Instrument}'");
                return 1;
            case AddSymbolResult.InvalidDigits:
                Console.WriteLine($"Invalid digits value: {options.Digits}");
                return 1;
        }

        try
        {
            config.Save(configPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save {configPath}: {ex.Message}");
            return 1;
        }

        if (!options.Quiet)
        {
            var action = options.Force && config.Digits.Count > 0 ? "Updated" : "Added";
            Console.WriteLine($"{action} {symbol} (digits={options.Digits}) in {configPath}.");
        }
        return 0;
    }

    /// <summary>
    /// Drives `cache remove-symbol`. Removes the symbol from all three
    /// sections of <c>instruments.json</c>. Prompts for confirmation
    /// unless <c>--no-prompt</c>. Never touches cached <c>.bi5</c> files.
    /// </summary>
    internal static int RunRemoveSymbol(CacheRemoveSymbolOptions options)
    {
        var symbol = options.Instrument?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(symbol))
        {
            Console.WriteLine("Pass --instrument SYM. Example: cache remove-symbol --instrument BTCUSD");
            return 1;
        }

        var configPath = options.InstrumentsConfigPath;
        var config = InstrumentConfig.Load(configPath);

        if (!config.Digits.ContainsKey(symbol)
            && !config.Earliest.ContainsKey(symbol)
            && !config.Latest.ContainsKey(symbol))
        {
            Console.WriteLine($"{symbol} is not in {configPath} (nothing to remove).");
            return 0;
        }

        if (!options.NonInteractive)
        {
            Console.Write($"Remove {symbol} from {configPath}? [y/N] ");
            var line = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (line != "y" && line != "yes")
            {
                Console.WriteLine("Cancelled. No changes made.");
                return 0;
            }
        }

        var result = InstrumentConfigEditor.Remove(config, symbol);
        if (!result.AnyRemoved)
        {
            // Defensive — the contains-check above should have caught this.
            Console.WriteLine($"{symbol} was not in any section.");
            return 0;
        }

        try
        {
            config.Save(configPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save {configPath}: {ex.Message}");
            return 1;
        }

        if (!options.Quiet)
        {
            var sections = new List<string>();
            if (result.RemovedFromDigits) sections.Add("digits");
            if (result.RemovedFromEarliest) sections.Add("earliest");
            if (result.RemovedFromLatest) sections.Add("latest");
            Console.WriteLine($"Removed {symbol} from {configPath} (sections: {string.Join(", ", sections)}).");
            Console.WriteLine($"  Note: cached .bi5 files for {symbol} are not touched. To reclaim disk space:");
            Console.WriteLine($"    cache cleanup --purge --instrument {symbol} --dry-run   # preview");
            Console.WriteLine($"    cache cleanup --purge --instrument {symbol}             # actually delete");
        }
        return 0;
    }

    /// <summary>
    /// Drives `cache size` — measures per-symbol (and optionally
    /// per-(symbol, year)) disk usage of the .bi5 cache and renders a
    /// sorted text table. CSV / file output is a planned follow-up.
    /// </summary>
    internal static int RunSize(CacheSizeOptions options)
    {
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        var sizer = new CacheSizer(poolPath);
        var symbolDirs = sizer.PlanSymbols(options.InstrumentFilter);
        var report = sizer.MeasureAll(symbolDirs);

        if (!report.PoolExists)
        {
            Console.WriteLine($"Pool path does not exist: {poolPath}");
            return 1;
        }

        Console.Write(options.ByYear ? report.RenderByYear(options.SortBy) : report.Render(options.SortBy));
        return 0;
    }

    internal static int RunAudit(CacheAuditOptions options)
    {
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        var auditor = new PoolAuditor(poolPath);
        var report = auditor.Audit(options.InstrumentFilter);
        Console.Write(report.Render());

        if (options.ByYear)
        {
            Console.WriteLine();
            Console.Write(report.RenderByYear());
        }
        if (options.EffectiveByMonth)
        {
            Console.WriteLine();
            Console.Write(report.RenderByMonth());
        }

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

        var plan = verifier.PlanFiles(options.InstrumentFilter, options.StartUtc, options.EndUtc);
        if (plan.Count == 0)
        {
            var scopeLabel = (options.StartUtc, options.EndUtc) switch
            {
                (null, null) => "",
                (var s, null) => $" since {s:yyyy-MM-dd}",
                (null, var e) => $" up to {e:yyyy-MM-dd}",
                (var s, var e) => $" between {s:yyyy-MM-dd} and {e:yyyy-MM-dd}"
            };
            Console.WriteLine($"No .bi5 files to verify in {poolPath}{scopeLabel}");
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
            var scopeLabel = (options.StartUtc, options.EndUtc) switch
            {
                (null, null) => "",
                (var s, null) => $" since {s:yyyy-MM-dd}",
                (null, var e) => $" up to {e:yyyy-MM-dd}",
                (var s, var e) => $" between {s:yyyy-MM-dd} and {e:yyyy-MM-dd}"
            };
            if (options.Remote)
            {
                var mode = options.SizeOnly ? "size-only" : "byte-exact";
                Console.WriteLine($"Verifying {plan.Count:N0} cached file(s) in {poolPath}{scopeLabel} (local + remote drift, {mode}, parallel={options.Parallel})...");
            }
            else
            {
                Console.WriteLine($"Verifying {plan.Count:N0} cached file(s) in {poolPath}{scopeLabel}...");
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
                report = await verifier.RunPlanWithRemoteAsync(plan, probe, byteExact: !options.SizeOnly, parallelism: options.Parallel, cts.Token);
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

        // --purge branch: wipe one or more symbol subdirectories entirely.
        // Different operation from the regular junk-cleanup walk; needs an
        // explicit symbol filter so we never nuke the whole pool by accident.
        if (options.Purge)
        {
            return RunPurge(options, poolPath);
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

    /// <summary>
    /// `cache cleanup --purge` path: wipe one or more symbol subdirectories
    /// entirely. Different from the regular junk-cleanup walk — this is the
    /// "I just ran cache remove-symbol, now reclaim the disk space" operation.
    /// Requires <c>--instrument SYM</c> or <c>--symbols A,B,...</c>; we
    /// refuse to operate without an explicit symbol filter so the pool can
    /// never be wiped wholesale by accident.
    /// </summary>
    private static int RunPurge(CacheCleanupOptions options, string poolPath)
    {
        if (options.InstrumentFilter is null || options.InstrumentFilter.Count == 0)
        {
            Console.WriteLine("--purge requires --instrument SYM or --symbols A,B,...");
            return 1;
        }

        var cleaner = new CacheCleaner(poolPath);
        var reports = new List<CachePurgeReport>();
        var anyFailures = false;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        foreach (var symbol in options.InstrumentFilter)
        {
            if (cts.IsCancellationRequested) break;
            if (!options.Quiet)
            {
                Console.WriteLine(options.DryRun
                    ? $"Dry-run: planning purge of {symbol} from {poolPath}..."
                    : $"WARNING: about to DELETE every cached file for {symbol} from {poolPath}.");
            }

            CachePurgeReport report;
            try
            {
                report = cleaner.PurgeSymbol(symbol, options.DryRun, cts.Token);
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.WriteLine("Purge cancelled.");
                return 1;
            }

            Console.WriteLine();
            Console.Write(report.Render());
            reports.Add(report);
            anyFailures |= report.AnyFailures;
        }

        return anyFailures ? 1 : 0;
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
        Console.WriteLine("  cache audit    [--instrument SYM] [--by-year] [--by-month | --no-by-month]");
        Console.WriteLine("                 Inspect the cache: file counts, coverage, disk usage.");
        Console.WriteLine("                 --by-year / --by-month add finer-grained coverage grids; month is auto-included for single-symbol audits.");
        Console.WriteLine("  cache size     [--instrument SYM] [--by-year] [--sort size|symbol|year|files]");
        Console.WriteLine("                 Disk-usage breakdown by symbol (and optionally year). Sorted by --sort (default: size, largest first).");
        Console.WriteLine("  cache add-symbol     --instrument SYM --digits N [--force] [--no-verify-source]");
        Console.WriteLine("                       Register a new symbol in instruments.json. Probes Dukascopy by default; --force overwrites existing.");
        Console.WriteLine("  cache remove-symbol  --instrument SYM [--no-prompt]");
        Console.WriteLine("                       Drop a symbol from instruments.json (all three sections). Prompts unless --no-prompt.");
        Console.WriteLine("  cache verify   [--instrument SYM] [--start ISO] [--end ISO]");
        Console.WriteLine("                 [--remote [--size-only] [--parallel N]] [--quiet]");
        Console.WriteLine("                 Recompute SHA-256 vs sidecar (local) and optionally probe Dukascopy for drift.");
        Console.WriteLine("                 --start/--end scope to a date range (day precision). --parallel defaults to 8 concurrent probes.");
        Console.WriteLine("  cache repair   [--instrument SYM] [--dry-run] [--trust-existing] [--quiet]");
        Console.WriteLine("                 Auto-fix files flagged by verify (refetch from Dukascopy or regenerate sidecar).");
        Console.WriteLine("  cache cleanup  [--instrument SYM] [--dry-run] [--purge] [--quiet]");
        Console.WriteLine("                 Default: DELETES zero-byte .bi5, orphan .meta.json, and .tmp files. Use --dry-run first.");
        Console.WriteLine("                 --purge --instrument SYM wipes the symbol's entire pool subdirectory (after cache remove-symbol).");
        Console.WriteLine("  export bars    --instrument SYM --start ISO --end ISO --timeframe TF [--format csv|csv+hst]");
        Console.WriteLine("                 Read cache (offline), write MT5 bar CSV/HST.");
        Console.WriteLine("  export ticks   --instrument SYM --start ISO --end ISO");
        Console.WriteLine("                 Read cache (offline), write per-month tick CSVs (MT5 import format).");
        Console.WriteLine();
        Console.WriteLine("Common flags accepted by most subcommands:");
        Console.WriteLine("  --instrument SYM        Single symbol (default: EURUSD).");
        Console.WriteLine("  --symbols A,B,C|all     Comma-separated list, or 'all' for every entry in instruments.json.");
        Console.WriteLine("  --digits N              Force digits for all symbols this run (overrides instruments.json).");
        Console.WriteLine("  --digits-map SYM=N,...  Per-symbol digits override (e.g. --digits-map XAUUSD=3,USDJPY=3).");
        Console.WriteLine("  --start ISO             ISO 8601 start time (e.g. 2025-01-01T00:00:00Z).");
        Console.WriteLine("  --end ISO               ISO 8601 end time.");
        Console.WriteLine("  --pool PATH             Cache root (default: D:\\MarketData).");
        Console.WriteLine("  --output PATH           Output root for exports (default: D:\\MarketData\\Exports).");
        Console.WriteLine("  --instruments PATH      Path to instruments.json (default: src/ConsoleApp/Config/instruments.json).");
        Console.WriteLine("  --http PATH             Path to http.json (default: src/ConsoleApp/Config/http.json).");
        Console.WriteLine("  --no-prompt             Skip any interactive prompts (cache add-symbol, cache remove-symbol).");
        Console.WriteLine("  --quiet                 Silence progress bars, banners, URL trace, and per-symbol summaries.");
        Console.WriteLine("  --verbose               Print every download URL (off by default).");
        Console.WriteLine();
        Console.WriteLine("Export-specific flags (export bars / export ticks):");
        Console.WriteLine("  --timeframe TF          m1|m5|m15|m30|h1|h4|h6|d1|w1|mn1|m<minutes>  (export bars only)");
        Console.WriteLine("  --format csv|csv+hst    Output format (export bars only; default csv+hst).");
        Console.WriteLine("  --offset +HH:MM         Fixed UTC->server-local offset (mutually exclusive with --broker).");
        Console.WriteLine("  --broker NAME           DST-aware broker preset. Supported: ic-markets.");
        Console.WriteLine("                          (mutually exclusive with --offset.)");
        Console.WriteLine("  --spread-method M       last|min|mean|median  (export bars only; default last).");
        Console.WriteLine("                          Controls per-bar Spread aggregation across ticks and at resample.");
        Console.WriteLine();
        Console.WriteLine("Cache-update specific flags (cache update / cache catchup):");
        Console.WriteLine("  --mode ticks|direct     Source: hourly tick files (default) or daily M1 bars.");
        Console.WriteLine("  --no-refresh            Skip re-fetch of cached files (default refresh is ON).");
        Console.WriteLine("  --recent-refresh-days N When --refresh is on, files newer than N days are re-fetched (default 30).");
        Console.WriteLine("  --no-verify-checksum    Skip checksum validation (verify is ON by default).");
        Console.WriteLine("  --no-dedupe             Disable tick de-duplication.");
        Console.WriteLine("  --allow-fallback-overlap   Allow fallback M1 bars to merge with tick minutes.");
        Console.WriteLine("  --no-repair-gaps        Disable gap repair (gap repair is ON by default).");
        Console.WriteLine("  --no-validate-m1        Disable M1 validation (validate is ON by default).");
        Console.WriteLine("  --validation-tolerance-points N    OHLC tolerance for M1 validation (default 1).");
        Console.WriteLine("  --use-session-calendar  Enable session calendar filtering.");
        Console.WriteLine("  --session-config PATH   Path to sessions.json.");
        Console.WriteLine();
        Console.WriteLine("Subcommand-specific flags are documented under each command's --help equivalent in README.md.");
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

}
