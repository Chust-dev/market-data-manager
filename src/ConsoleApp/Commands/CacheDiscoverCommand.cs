using HistoricalData.Manage;
using HistoricalData.Commands.Options;
using HistoricalData.Config;
using HistoricalData.Utils;

namespace HistoricalData.Commands;

/// <summary>
/// `cache discover` — find the earliest UTC hour Dukascopy has data for
/// each requested symbol and record it in instruments.json (the same
/// file that holds the digits map). Lives in the Manage pillar:
/// read-only, no cache mutation, just learns about the source.
///
/// Default behaviour is **idempotent** — symbols that already have an
/// `earliest` entry in instruments.json are skipped, so re-running on a
/// schedule is cheap. Pass `--refresh` to re-probe all requested symbols
/// (useful when adding a new listing or after a long gap).
///
/// Modes:
///   cache discover --instrument EURUSD                     (single symbol)
///   cache discover --symbols EURUSD,USDJPY,GBPUSD          (explicit list)
///   cache discover --symbols all                           (every key in `digits`)
///   cache discover --symbols all --refresh                 (re-probe everything)
///   cache discover --symbols all --since 2010-01-01        (tighter lower bound)
///   cache discover --symbols all --parallel 8              (more aggressive)
///   cache discover --symbols all --output ./discovery.json (write to a different file)
/// </summary>
public sealed class CacheDiscoverCommand : ICommand
{
    public async Task<int> RunAsync(string[] args)
    {
        var argMap = ArgParser.Parse(args);
        var options = CacheDiscoverOptions.FromArgs(argMap);

        // Load existing instruments.json so we can both filter and merge.
        var configPath = options.InstrumentsConfigPath;
        var instrumentConfig = InstrumentConfig.Load(configPath);

        // The output target defaults to instruments.json — the user explicitly
        // asked for discoveries to live alongside the digits map. `--output`
        // overrides for one-off / experimental runs.
        var savePath = options.OutputPath ?? configPath;

        // Resolve the symbol set. Priority: --symbols (list or "all") wins,
        // then --instrument, then fall back to every key already in the digits map.
        var targets = ResolveTargets(argMap, options, instrumentConfig);
        if (targets.Count == 0)
        {
            Console.WriteLine("No symbols to discover. Pass --instrument SYM, --symbols all, or --symbols A,B,C.");
            return 1;
        }

        // Idempotent filter — skip symbols already discovered, unless --refresh.
        var toDiscover = options.RefreshDiscovery
            ? targets
            : targets.Where(s => !instrumentConfig.Earliest.ContainsKey(s)).ToList();

        if (toDiscover.Count == 0)
        {
            if (!options.Quiet)
            {
                Console.WriteLine($"All {targets.Count} symbol(s) already discovered. Use --refresh to re-probe.");
            }
            return 0;
        }

        // Build the network client. Discovery is read-only on the cache —
        // the pool path is only needed because DukascopyClient takes one
        // (it would otherwise be used for `.bi5` writes that we never do).
        var httpConfig = HttpConfig.Load(options.HttpConfigPath);
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        Directory.CreateDirectory(poolPath);
        var client = new DukascopyClient(httpConfig, poolPath, options.Verbose);
        var discoverer = new CacheDiscoverer(client);

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            if (!options.Quiet)
            {
                Console.WriteLine();
                Console.WriteLine("Cancellation requested. Saving partial results...");
            }
        };
        Console.CancelKeyPress += cancelHandler;

        var until = DateTimeOffset.UtcNow;
        var resultsLock = new object();
        var canceled = false;

        try
        {
            if (!options.Quiet)
            {
                Console.WriteLine(
                    $"Discovering {toDiscover.Count} symbol(s) since {options.Since:yyyy-MM-dd} (parallel={options.Parallel}).");
                Console.WriteLine($"  {"Symbol",-10}  {"Earliest UTC",-22}  {"Probes",-7}  Status");
            }

            await Parallel.ForEachAsync(
                toDiscover,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, options.Parallel),
                    CancellationToken = cts.Token
                },
                async (symbol, ct) =>
                {
                    var result = await discoverer.DiscoverAsync(symbol, options.Since, until, ct);

                    // Mutating the shared dict while ForEachAsync runs other branches —
                    // serialize the write so we don't trip the dictionary's invariants.
                    lock (resultsLock)
                    {
                        instrumentConfig.Earliest[symbol] = result.Earliest;
                    }

                    if (!options.Quiet)
                    {
                        var earliestStr = result.Earliest.HasValue
                            ? result.Earliest.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                            : "—";
                        var status = result.Note ?? (result.Earliest.HasValue ? "ok" : "unknown");
                        // One-shot per-symbol line. Console.WriteLine is fine to
                        // race here — it's atomic on Windows for small writes and
                        // the worst case is interleaved lines, never garbled chars.
                        Console.WriteLine($"  {symbol,-10}  {earliestStr,-22}  {result.Probes,-7}  {status}");
                    }
                });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            canceled = true;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }

        // Always persist whatever we learned, even on cancel — partial
        // progress is still useful and re-running with --refresh will
        // re-probe anything that didn't finish.
        try
        {
            instrumentConfig.Save(savePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save {savePath}: {ex.Message}");
            return 1;
        }

        if (!options.Quiet)
        {
            var savedCount = instrumentConfig.Earliest.Count;
            Console.WriteLine();
            Console.WriteLine(canceled
                ? $"Canceled. Saved {savedCount} earliest entries to {savePath}."
                : $"Wrote {savedCount} earliest entries to {savePath}.");
        }

        return canceled ? 130 : 0;
    }

    /// <summary>
    /// Build the list of symbols to discover, in priority order:
    /// 1. <c>--symbols all</c> → every key in <c>digits</c> (config-driven).
    /// 2. <c>--symbols A,B,C</c> → exactly those, uppercased.
    /// 3. <c>--instrument SYM</c> → just that one.
    /// 4. nothing → every key in <c>digits</c>.
    /// </summary>
    private static List<string> ResolveTargets(
        IReadOnlyDictionary<string, string> argMap,
        CacheDiscoverOptions options,
        InstrumentConfig instrumentConfig)
    {
        var symbols = argMap.GetValueOrDefault("symbols");
        if (!string.IsNullOrWhiteSpace(symbols))
        {
            if (symbols.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                return instrumentConfig.Digits.Keys
                    .Select(s => s.ToUpperInvariant())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return symbols
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (argMap.ContainsKey("instrument") && !string.IsNullOrWhiteSpace(options.Instrument))
        {
            return [options.Instrument.ToUpperInvariant()];
        }

        return instrumentConfig.Digits.Keys
            .Select(s => s.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
