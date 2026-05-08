using HistoricalData.Commands.Options;
using HistoricalData.Config;
using HistoricalData.Export;
using HistoricalData.Models;
using HistoricalData.Utils;

namespace HistoricalData.Download;

/// <summary>
/// Download pillar — fills the .bi5 cache from Dukascopy. Network calls
/// originate here and only here. The other two pillars (Display, Export)
/// are entirely cache-only.
///
/// Honours the same refresh-policy flags as the legacy CLI: by default
/// every requested hour is fetched fresh; pass <c>--no-refresh</c> to
/// skip files older than <c>--recent-refresh-days</c> (default 30).
/// Use as a daily / weekly maintenance pass.
///
/// Implementation note: the engine method
/// <see cref="DukascopyClient.DownloadTicksAndAggregate"/> requires a
/// <see cref="BarAggregator"/> argument and feeds ticks through it as
/// they arrive. Pure cache-update doesn't need the resulting bars; for
/// now we pass a throwaway aggregator and discard its output. A future
/// commit can add a true download-only method to <see cref="DukascopyClient"/>
/// that skips the aggregator overhead.
/// </summary>
internal sealed class Downloader
{
    public async Task<int> RunAsync(CacheUpdateOptions options)
    {
        // 1. Load configs
        var httpConfig = HttpConfig.Load(options.HttpConfigPath);
        var instrumentConfig = InstrumentConfig.Load(options.InstrumentsConfigPath);

        // 2. Resolve instruments
        var requestedInstruments = Program.ResolveRequestedInstruments(options.Instrument, options.Instruments, instrumentConfig).ToList();
        if (requestedInstruments.Count == 0)
        {
            Console.WriteLine("No instruments selected.");
            return 1;
        }

        // 3. Path
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        Directory.CreateDirectory(poolPath);

        // 4. Client (this pillar's the one that actually uses the network)
        var client = new DukascopyClient(httpConfig, poolPath, options.Verbose);

        // 5. Cancellation handler
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Cancellation requested. Finishing current operation...");
        };

        // 6. Date window
        var startUtc = options.Start.ToUniversalTime();
        var endUtc = options.End.ToUniversalTime();
        if (endUtc < startUtc)
        {
            Console.WriteLine("End time must be after start time.");
            return 1;
        }

        // 7. Per-instrument loop
        var succeeded = 0;
        var failed = 0;
        var cancelled = 0;
        var digitsMapOverrides = Program.ParseDigitsMap(options.DigitsMap);

        foreach (var instrument in requestedInstruments)
        {
            if (cts.IsCancellationRequested) break;
            try
            {
                // Probe — for download we DO want to know if the symbol exists
                // on Dukascopy at all before spending time iterating its hours.
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

                var digits = await Program.ResolveDigitsAsync(
                    client, instrument, options.Digits,
                    instrumentConfig, digitsMapOverrides,
                    startUtc, endUtc, cts.Token);

                await UpdateInstrumentCacheAsync(
                    client, instrument, digits,
                    options, startUtc, endUtc, cts.Token);
                succeeded++;
            }
            // Real user cancellation (Ctrl+C) — cts.Token was triggered.
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                Console.WriteLine($"Canceled while processing {instrument}.");
                cancelled++;
                break;
            }
            // Any other exception (including HttpClient TaskCanceledException
            // = request timeout, which inherits from OperationCanceledException
            // but isn't triggered by cts) is a per-instrument failure. Loop
            // continues to the next symbol.
            catch (Exception ex)
            {
                Console.WriteLine($"Failed instrument {instrument}: {ex.Message}");
                failed++;
            }
        }

        // 8. Batch summary
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
    /// Per-instrument download work. Calls the engine method that fills the
    /// cache (with a throwaway aggregator), then optionally triggers the
    /// gap-repair and m1-validation passes which are also cache-filling
    /// operations (they download daily M1 files; the comparison side-effect
    /// is moot for cache update).
    /// </summary>
    private static async Task UpdateInstrumentCacheAsync(
        DukascopyClient client,
        string instrument,
        int digits,
        CacheUpdateOptions options,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {instrument} ===");

        var summary = new SummaryReport();

        // Throwaway aggregator — discarded after the call returns.
        // The DownloadTicksAndAggregate API requires it for now.
        var aggregator = new BarAggregator(
            "m1", digits, TimeSpan.Zero, filterWeekends: true,
            startUtc, endUtc, deduplicateTicks: true, skipFallbackIfTicked: true,
            sessionCalendar: null);

        if (options.DownloadMode == DownloadMode.TickToM1)
        {
            await client.DownloadTicksAndAggregate(
                instrument, startUtc, endUtc, digits,
                fallbackToM1: true,
                options.RefreshCache, options.VerifyChecksum, options.RecentRefreshDays,
                aggregator, summary, cancellationToken);
        }
        else
        {
            await client.DownloadM1Bars(
                instrument, startUtc, endUtc, digits,
                options.RefreshCache, options.VerifyChecksum, options.RecentRefreshDays,
                aggregator, summary, cancellationToken);
        }

        // Gap-repair pass — downloads daily M1 files for the date range.
        // Bars get filled in memory; we discard them. The cache-fill side
        // effect is what we want.
        if (options.RepairGaps && options.DownloadMode == DownloadMode.TickToM1)
        {
            var repairAggregator = new BarAggregator(
                "m1", digits, TimeSpan.Zero, filterWeekends: true,
                startUtc, endUtc, deduplicateTicks: false, skipFallbackIfTicked: true,
                sessionCalendar: null);
            var repairSummary = new SummaryReport();
            await client.DownloadM1Bars(
                instrument, startUtc, endUtc, digits,
                options.RefreshCache, options.VerifyChecksum, options.RecentRefreshDays,
                repairAggregator, repairSummary, cancellationToken);
        }

        // Validate-m1 pass — same idea: another set of daily M1 downloads.
        // The bar comparison that happens during export doesn't apply here;
        // we're only after the cache fill.
        if (options.ValidateM1 && options.DownloadMode == DownloadMode.TickToM1)
        {
            var validateAggregator = new BarAggregator(
                "m1", digits, TimeSpan.Zero, filterWeekends: true,
                startUtc, endUtc, deduplicateTicks: false, skipFallbackIfTicked: false,
                sessionCalendar: null);
            var validateSummary = new SummaryReport();
            await client.DownloadM1Bars(
                instrument, startUtc, endUtc, digits,
                options.RefreshCache, options.VerifyChecksum, options.RecentRefreshDays,
                validateAggregator, validateSummary, cancellationToken);
        }

        summary.Print();
    }
}
