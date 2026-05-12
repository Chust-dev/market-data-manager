using HistoricalData.Commands.Options;
using HistoricalData.Config;
using HistoricalData.Models;
using HistoricalData.Utils;

namespace HistoricalData.Export;

/// <summary>
/// Export pillar — produces MT5-compatible bar files (CSV and/or HST) by
/// reading the .bi5 cache, aggregating ticks into M1 bars, optionally
/// resampling to a higher timeframe, and writing the result.
///
/// This pillar never reaches the network. The cache must already be filled
/// (e.g. by a prior <c>cache update</c> run). Hours that aren't in the
/// cache are silently skipped, matching the cache-only semantics added in
/// the Layer 1 refactor.
///
/// Takes a typed <see cref="BarExportOptions"/> directly — no
/// <see cref="AppOptions"/> dependency. Calls into shared helpers on
/// <see cref="Program"/> for instrument resolution / digits resolution.
/// Layer 3+ may relocate those helpers to a dedicated pipeline-helpers
/// class; for now the existing locations are stable.
/// </summary>
internal sealed class BarExporter
{
    public async Task<int> RunAsync(BarExportOptions options)
    {
        // 1. Load configs, validate timeframe
        var httpConfig = HttpConfig.Load(options.HttpConfigPath);
        var instrumentConfig = InstrumentConfig.Load(options.InstrumentsConfigPath);

        if (!TimeframeUtils.TryParse(options.Timeframe, out var timeframeInfo))
        {
            Console.WriteLine($"Unsupported timeframe. {TimeframeUtils.SupportedTimeframeHint}");
            return 1;
        }

        // 2. Resolve which instruments the user actually wants
        var requestedInstruments = Program.ResolveRequestedInstruments(options.Instrument, options.Instruments, instrumentConfig).ToList();
        if (requestedInstruments.Count == 0)
        {
            Console.WriteLine("No instruments selected.");
            return 1;
        }

        // 3. Build paths, ensure they exist
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        var outputPath = PathUtils.NormalizePath(options.OutputPath);
        Directory.CreateDirectory(poolPath);
        Directory.CreateDirectory(outputPath);

        // 4. Cache reader (still named DukascopyClient for now; Layer 3 may
        // rename to Downloader/CacheReader once the download path is fully
        // separated). The export pillar uses it only for cache reads, never
        // network calls — verifyChecksum/refreshCache are forced off below.
        var client = new DukascopyClient(httpConfig, poolPath, options.Verbose);

        // 5. Cancellation handler — same Ctrl+C behaviour as the legacy CLI
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.WriteLine("Cancellation requested. Finishing current operation...");
        };

        // 6. Validate the time window
        var startUtc = options.Start.ToUniversalTime();
        var endUtc = options.End.ToUniversalTime();
        if (endUtc < startUtc)
        {
            Console.WriteLine("End time must be after start time.");
            return 1;
        }

        // 7. Optional session-calendar filter for the aggregator
        SessionConfig.SessionCalendar? sessionCalendar = null;
        if (options.UseSessionCalendar)
        {
            var sessionConfig = SessionConfig.Load(options.SessionConfigPath);
            sessionCalendar = new SessionConfig.SessionCalendar(sessionConfig);
        }

        // 8. Per-instrument loop
        var succeeded = 0;
        var failed = 0;
        var cancelled = 0;
        var digitsMapOverrides = Program.ParseDigitsMap(options.DigitsMap);
        var multipleInstruments = requestedInstruments.Count > 1;

        foreach (var instrument in requestedInstruments)
        {
            if (cts.IsCancellationRequested) break;
            try
            {
                // Cache-only mode: don't bother probing Dukascopy. If the
                // symbol's cache is empty, the per-instrument run produces
                // an empty output (still considered "succeeded" — the cache
                // simply doesn't contain that symbol).
                var digits = await Program.ResolveDigitsAsync(
                    client, instrument, options.Digits,
                    instrumentConfig, digitsMapOverrides,
                    startUtc, endUtc, cts.Token);

                await ExportInstrumentAsync(
                    client, instrument, digits,
                    options, startUtc, endUtc,
                    timeframeInfo, outputPath, sessionCalendar,
                    multipleInstruments,
                    cts.Token);
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
            // = request timeout) is a per-instrument failure. Loop continues.
            catch (Exception ex)
            {
                Console.WriteLine($"Failed instrument {instrument}: {ex.Message}");
                failed++;
            }
        }

        // 9. Batch summary — only when more than one instrument was requested.
        // For single-symbol runs the per-instrument Summary block already says
        // whether it worked, so the Requested/Succeeded/Failed tally is noise.
        if (multipleInstruments)
        {
            Console.WriteLine();
            Console.WriteLine("Batch summary:");
            Console.WriteLine($"  Requested: {requestedInstruments.Count}");
            Console.WriteLine($"  Succeeded: {succeeded}");
            Console.WriteLine($"  Failed:    {failed}");
            if (cancelled > 0)
            {
                Console.WriteLine($"  Cancelled: {cancelled}");
            }
        }
        if (cancelled > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Run cancelled — partial output committed. Re-run to resume.");
        }

        return failed == 0 && cancelled == 0 ? 0 : 1;
    }

    /// <summary>
    /// The per-instrument bar-export work: read cache, aggregate, optional
    /// resample, write outputs. No gap repair, no M1 validation, no tick
    /// CSV side-effect — those are concerns of other commands or the
    /// download pillar.
    /// </summary>
    private static async Task ExportInstrumentAsync(
        DukascopyClient client,
        string instrument,
        int digits,
        BarExportOptions options,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        TimeframeInfo timeframeInfo,
        string outputPath,
        SessionConfig.SessionCalendar? sessionCalendar,
        bool multipleInstruments,
        CancellationToken cancellationToken)
    {
        if (multipleInstruments)
        {
            Console.WriteLine();
            Console.WriteLine($"=== {instrument} ===");
        }

        var summary = new SummaryReport();
        var aggregator = new BarAggregator(
            "m1",
            digits,
            options.Offset,
            filterWeekends: true,
            startUtc,
            endUtc,
            options.DeduplicateTicks,
            options.SkipFallbackIfTicked,
            sessionCalendar,
            options.Spread);

        // Cache-only flags forced on. fallbackToM1=true means the daily M1
        // .bi5 (if cached) fills hours where tick files are absent — a read
        // operation, no network.
        await client.DownloadTicksAndAggregate(
            instrument,
            startUtc,
            endUtc,
            digits,
            fallbackToM1: true,
            refreshCache: false,
            verifyChecksum: false,
            recentRefreshDays: 0,
            aggregator,
            summary,
            cancellationToken);

        var m1Bars = aggregator.GetBars();
        var bars = m1Bars;
        if (timeframeInfo.Minutes > 1)
        {
            bars = BarResampler.Resample(bars, timeframeInfo, options.Spread);
        }

        summary.Bars = bars.Count;
        summary.DuplicateTicksDropped = aggregator.DuplicateTicksDropped;
        summary.FallbackBarsSkipped = aggregator.FallbackBarsSkipped;

        string? csvPath = null;
        string? hstPath = null;
        if (options.OutputFormat != OutputFormat.None)
        {
            csvPath = Path.Combine(outputPath, $"{instrument}_{options.Timeframe}.csv");
            CsvWriter.Write(csvPath, bars);

            if (options.OutputFormat == OutputFormat.CsvHst)
            {
                hstPath = Path.Combine(outputPath, $"{instrument}_{options.Timeframe}.hst");
                HstWriter.Write(hstPath, bars, instrument, digits, timeframeInfo.Minutes);
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
    }
}
