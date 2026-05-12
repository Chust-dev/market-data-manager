using HistoricalData.Commands.Options;
using HistoricalData.Config;
using HistoricalData.Utils;

namespace HistoricalData.Export;

/// <summary>
/// Export pillar — produces per-month tick CSV files in MT5 Symbol Editor
/// import format by walking the .bi5 cache. No bar aggregation, no
/// resampling — each cached hour is decoded and its ticks streamed
/// directly to the active monthly file.
///
/// Cache-only by design: never reaches the network. Hours not in the
/// cache are silently skipped. Output: one file per calendar month,
/// named <c>SYMBOL_ticks_yyyy-MM.csv</c>.
/// </summary>
internal sealed class TickExporter
{
    public async Task<int> RunAsync(TickExportOptions options)
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

        // 3. Paths
        var poolPath = PathUtils.NormalizePath(options.PoolPath);
        var outputPath = PathUtils.NormalizePath(options.OutputPath);
        Directory.CreateDirectory(poolPath);
        Directory.CreateDirectory(outputPath);

        // 4. Cache reader
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
                var digits = await Program.ResolveDigitsAsync(
                    client, instrument, options.Digits,
                    instrumentConfig, digitsMapOverrides,
                    startUtc, endUtc, cts.Token);

                await ExportTicksForInstrumentAsync(
                    client, instrument, digits,
                    options.Offset, outputPath,
                    startUtc, endUtc, cts.Token);
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
            Console.WriteLine("Run cancelled — partial output committed. Re-run to resume.");
        }

        return failed == 0 && cancelled == 0 ? 0 : 1;
    }

    /// <summary>
    /// The per-instrument tick-export work: open a writer, walk the cached
    /// .bi5 files via <see cref="DukascopyClient.ExportTicksToCsvAsync"/>,
    /// close the writer, report what got written.
    /// </summary>
    private static async Task ExportTicksForInstrumentAsync(
        DukascopyClient client,
        string instrument,
        int digits,
        BrokerOffset broker,
        string outputPath,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {instrument} ===");

        using var writer = new TickCsvWriter(outputPath, instrument, digits, broker);
        var exportedTicks = await client.ExportTicksToCsvAsync(
            instrument,
            startUtc,
            endUtc,
            digits,
            writer,
            cancellationToken);

        Console.WriteLine($"Tick CSV: {exportedTicks:N0} ticks across {writer.FilesOpened} monthly file(s) in {outputPath}");
    }
}
