using System.Net;
using HistoricalData.Config;
using HistoricalData.DataPool;
using HistoricalData.Export;
using HistoricalData.Models;
using HistoricalData.Utils;
using SevenZip.Compression.LZMA;

namespace HistoricalData;

public sealed class DukascopyClient
{
    private const string TickFileSuffix = "ticks";
    private const string M1DayFileName = "BID_candles_min_1.bi5";
    private const int TickRecordSize = 20;
    private const int M1RecordSize = 24;
    private readonly HttpConfig _config;
    private readonly DataPool.DataPool _pool;
    private readonly HttpClient _httpClient;
    private readonly bool _verbose;
    private readonly int _maxConcurrency;

    public DukascopyClient(HttpConfig config, string poolPath, bool verbose)
    {
        _config = config;
        _pool = new DataPool.DataPool(poolPath);
        _verbose = verbose;
        _maxConcurrency = Math.Max(2, Math.Min(8, Environment.ProcessorCount));
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
        };
    }

    public async Task<SymbolProbeResult> ProbeSymbolAvailabilityAsync(
        string instrument,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        var hasTransientError = false;
        string? lastError = null;

        foreach (var hourUtc in GetProbeHours(startUtc, endUtc))
        {
            var fileName = $"{hourUtc:HH}h_{TickFileSuffix}.bi5";
            foreach (var month in GetMonthCandidates(hourUtc.Month))
            {
                var relativePath = $"{instrument}/{hourUtc:yyyy}/{month:00}/{hourUtc:dd}/{fileName}";
                foreach (var baseUrl in _config.BaseUrls)
                {
                    var probe = await ProbeUrlAsync($"{baseUrl.TrimEnd('/')}/{relativePath}", cancellationToken);
                    if (probe.Found)
                    {
                        return new SymbolProbeResult(SymbolProbeStatus.Available, null);
                    }

                    if (!probe.NotFound)
                    {
                        hasTransientError = true;
                        lastError = probe.ErrorMessage ?? lastError;
                    }
                }
            }
        }

        foreach (var dayUtc in GetProbeDays(startUtc, endUtc))
        {
            foreach (var month in GetMonthCandidates(dayUtc.Month))
            {
                var relativePath = $"{instrument}/{dayUtc:yyyy}/{month:00}/{dayUtc:dd}/{M1DayFileName}";
                foreach (var baseUrl in _config.BaseUrls)
                {
                    var probe = await ProbeUrlAsync($"{baseUrl.TrimEnd('/')}/{relativePath}", cancellationToken);
                    if (probe.Found)
                    {
                        return new SymbolProbeResult(SymbolProbeStatus.Available, null);
                    }

                    if (!probe.NotFound)
                    {
                        hasTransientError = true;
                        lastError = probe.ErrorMessage ?? lastError;
                    }
                }
            }
        }

        return hasTransientError
            ? new SymbolProbeResult(SymbolProbeStatus.TransientError, lastError)
            : new SymbolProbeResult(SymbolProbeStatus.NotFound, null);
    }

    /// <summary>
    /// Probe a single hour on Dukascopy to see if its tick file exists.
    /// Walks the same month-candidates × base-URLs grid as the downloader,
    /// so a 200 from any combination counts as Available. Doesn't write
    /// anything to the local cache.
    ///
    /// Used by <see cref="HistoricalData.Audit.CacheDiscoverer"/> to
    /// binary-search for the earliest day a symbol has data on the source.
    /// </summary>
    public async Task<HourProbeResult> ProbeHourAvailableAsync(
        string instrument,
        DateTimeOffset hourUtc,
        CancellationToken cancellationToken = default)
    {
        var fileName = $"{hourUtc:HH}h_{TickFileSuffix}.bi5";
        var hasTransientError = false;

        foreach (var month in GetMonthCandidates(hourUtc.Month))
        {
            var relativePath = $"{instrument}/{hourUtc:yyyy}/{month:00}/{hourUtc:dd}/{fileName}";
            foreach (var baseUrl in _config.BaseUrls)
            {
                var probe = await ProbeUrlAsync($"{baseUrl.TrimEnd('/')}/{relativePath}", cancellationToken);
                if (probe.Found)
                {
                    return HourProbeResult.Available;
                }

                if (!probe.NotFound)
                {
                    hasTransientError = true;
                }
            }
        }

        return hasTransientError ? HourProbeResult.TransientError : HourProbeResult.NotFound;
    }

    public async Task<int?> TryDetectDigitsAsync(
        string instrument,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        CancellationToken cancellationToken = default)
    {
        foreach (var hourUtc in GetProbeHours(startUtc, endUtc))
        {
            var outcome = await DownloadToPoolAsync(
                instrument,
                hourUtc,
                TickFileSuffix,
                refreshCache: false,
                verifyChecksum: false,
                recentRefreshDays: 0,
                cancellationToken);
            if (!outcome.Success || outcome.LocalPath is null)
            {
                continue;
            }

            var compressed = await File.ReadAllBytesAsync(outcome.LocalPath, cancellationToken);
            var raw = DecompressLzma(compressed);
            var sample = ParseRawTickPrices(raw, sampleSize: 512);
            if (sample.Bids.Count == 0 || sample.Asks.Count == 0)
            {
                continue;
            }

            return InferDigits(sample.Bids, sample.Asks);
        }

        return null;
    }

    public async Task DownloadTicksAndAggregate(
        string instrument,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int digits,
        bool fallbackToM1,
        bool refreshCache,
        bool verifyChecksum,
        int recentRefreshDays,
        BarAggregator aggregator,
        SummaryReport summary,
        CancellationToken cancellationToken = default)
    {
        var aggregateLock = new object();
        var summaryLock = new object();
        var fallbackLock = new object();
        var fallbackDays = new HashSet<DateTimeOffset>();
        var semaphore = new SemaphoreSlim(_maxConcurrency);
        var tasks = new List<Task>();

        foreach (var hourUtc in TimeRangeUtils.EnumerateHours(startUtc, endUtc))
        {
            await semaphore.WaitAsync(cancellationToken);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessHourAsync(hourUtc);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        async Task ProcessHourAsync(DateTimeOffset hourUtc)
        {
            lock (summaryLock)
            {
                summary.HoursProcessed++;
            }

            var outcome = await DownloadToPoolAsync(
                instrument,
                hourUtc,
                TickFileSuffix,
                refreshCache,
                verifyChecksum,
                recentRefreshDays,
                cancellationToken);
            if (outcome.Success && outcome.LocalPath is not null)
            {
                var ticks = await ReadTicksAsync(outcome.LocalPath, hourUtc, digits, cancellationToken);
                if (ticks.Count > 0)
                {
                    lock (aggregateLock)
                    {
                        foreach (var tick in ticks)
                        {
                            aggregator.AddTick(tick);
                        }
                    }

                    lock (summaryLock)
                    {
                        summary.Ticks += ticks.Count;
                    }
                }

                return;
            }

            if (outcome.NotFound)
            {
                lock (summaryLock)
                {
                    summary.MissingHours++;
                }

                if (_verbose)
                {
                    Console.WriteLine($"Missing ticks for {instrument} {hourUtc:yyyy-MM-dd HH}:00Z");
                }
            }

            if (fallbackToM1)
            {
                var dayStart = new DateTimeOffset(hourUtc.Date, TimeSpan.Zero);
                var shouldDownload = false;
                lock (fallbackLock)
                {
                    if (!fallbackDays.Contains(dayStart))
                    {
                        fallbackDays.Add(dayStart);
                        shouldDownload = true;
                    }
                }

                if (!shouldDownload)
                {
                    return;
                }

                var fallbackBars = await DownloadM1BarsForDayData(
                    instrument,
                    dayStart,
                    digits,
                    refreshCache,
                    verifyChecksum,
                    recentRefreshDays,
                    cancellationToken);
                if (fallbackBars.Count > 0)
                {
                    lock (aggregateLock)
                    {
                        foreach (var bar in fallbackBars)
                        {
                            aggregator.AddBar(bar);
                        }
                    }

                    lock (summaryLock)
                    {
                        summary.M1FallbackBars += fallbackBars.Count;
                    }
                }
            }
        }
    }

    public async Task DownloadM1Bars(
        string instrument,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int digits,
        bool refreshCache,
        bool verifyChecksum,
        int recentRefreshDays,
        BarAggregator aggregator,
        SummaryReport summary,
        CancellationToken cancellationToken = default)
    {
        var aggregateLock = new object();
        var summaryLock = new object();
        var semaphore = new SemaphoreSlim(_maxConcurrency);
        var tasks = new List<Task>();

        foreach (var dayUtc in TimeRangeUtils.EnumerateDays(startUtc, endUtc))
        {
            await semaphore.WaitAsync(cancellationToken);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessDayAsync(dayUtc);
                }
                finally
                {
                    semaphore.Release();
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        async Task ProcessDayAsync(DateTimeOffset dayUtc)
        {
            var dayStart = new DateTimeOffset(dayUtc.UtcDateTime, TimeSpan.Zero);
            var dayEnd = dayStart.AddDays(1).AddTicks(-1);
            var hourCount = CountHoursInRange(dayStart, dayEnd, startUtc, endUtc);

            lock (summaryLock)
            {
                summary.HoursProcessed += hourCount;
            }

            var bars = await DownloadM1BarsForDayData(
                instrument,
                dayStart,
                digits,
                refreshCache,
                verifyChecksum,
                recentRefreshDays,
                cancellationToken);
            if (bars.Count == 0)
            {
                lock (summaryLock)
                {
                    summary.MissingHours += hourCount;
                }

                return;
            }

            lock (aggregateLock)
            {
                foreach (var bar in bars)
                {
                    aggregator.AddBar(bar);
                }
            }
        }
    }

    /// <summary>
    /// Sequential post-pass that walks the date range hour-by-hour, reads each cached
    /// .bi5 tick file (no network), decodes ticks, and feeds them to the supplied writer.
    /// Output is naturally sorted because we iterate in chronological order.
    /// Hours that aren't in the cache (or that fall outside the [startUtc, endUtc] window)
    /// are silently skipped.
    /// </summary>
    public async Task<long> ExportTicksToCsvAsync(
        string instrument,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int digits,
        TickCsvWriter writer,
        CancellationToken cancellationToken = default)
    {
        long totalTicks = 0;
        foreach (var hourUtc in TimeRangeUtils.EnumerateHours(startUtc, endUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = $"{hourUtc:HH}h_{TickFileSuffix}.bi5";
            string? localPath = null;
            // Check both 0-indexed and 1-indexed month folders to match the
            // candidates the downloader uses on the way in.
            foreach (var month in GetMonthCandidates(hourUtc.Month))
            {
                var candidate = _pool.GetLocalPath(instrument, hourUtc.Year, month, hourUtc.Day, fileName);
                if (DataPool.DataPool.HasValidFile(candidate))
                {
                    localPath = candidate;
                    break;
                }
            }

            if (localPath is null)
            {
                continue;
            }

            var ticks = await ReadTicksAsync(localPath, hourUtc, digits, cancellationToken);
            foreach (var tick in ticks)
            {
                if (tick.Time < startUtc || tick.Time >= endUtc)
                {
                    continue;
                }

                writer.Write(tick);
                totalTicks++;
            }
        }

        return totalTicks;
    }

    private async Task<IReadOnlyList<Bar>> DownloadM1BarsForDayData(
        string instrument,
        DateTimeOffset dayUtc,
        int digits,
        bool refreshCache,
        bool verifyChecksum,
        int recentRefreshDays,
        CancellationToken cancellationToken)
    {
        var outcome = await DownloadDailyToPoolAsync(
            instrument,
            dayUtc,
            M1DayFileName,
            refreshCache,
            verifyChecksum,
            recentRefreshDays,
            cancellationToken);
        if (!outcome.Success || outcome.LocalPath is null)
        {
            if (outcome.NotFound && _verbose)
            {
                Console.WriteLine($"Missing M1 bars for {instrument} {dayUtc:yyyy-MM-dd}");
            }

            return Array.Empty<Bar>();
        }

        return await ReadBarsAsync(outcome.LocalPath, dayUtc, digits, cancellationToken);
    }

    private async Task<DownloadOutcome> DownloadToPoolAsync(
        string instrument,
        DateTimeOffset hourUtc,
        string suffix,
        bool refreshCache,
        bool verifyChecksum,
        int recentRefreshDays,
        CancellationToken cancellationToken)
    {
        var fileName = $"{hourUtc:HH}h_{suffix}.bi5";
        var monthCandidates = new List<int>
        {
            hourUtc.Month - 1,
            hourUtc.Month
        }.Where(m => m is >= 0 and <= 12).Distinct().ToList();

        var cacheOnly = !refreshCache && recentRefreshDays <= 0;

        if (!ShouldRefresh(hourUtc, refreshCache, recentRefreshDays))
        {
            foreach (var month in monthCandidates)
            {
                var localPath = _pool.GetLocalPath(instrument, hourUtc.Year, month, hourUtc.Day, fileName);
                if (TryUseCache(localPath, verifyChecksum))
                {
                    return DownloadOutcome.FromCache(localPath);
                }
            }

            // User explicitly asked to use cache only (--no-refresh + recent-refresh-days <= 0).
            // Skip the network entirely; missing hours are simply unavailable.
            if (cacheOnly)
            {
                return DownloadOutcome.CreateNotFound();
            }
        }

        var anyNotFound = true;
        var lastError = string.Empty;

        for (var attempt = 1; attempt <= _config.RetryCount; attempt++)
        {
            anyNotFound = true;
            foreach (var month in monthCandidates)
            {
                var relativePath = $"{instrument}/{hourUtc:yyyy}/{month:00}/{hourUtc:dd}/{fileName}";
                var localPath = _pool.GetLocalPath(instrument, hourUtc.Year, month, hourUtc.Day, fileName);
                foreach (var baseUrl in _config.BaseUrls)
                {
                    var url = $"{baseUrl.TrimEnd('/')}/{relativePath}";
                    var result = await TryDownloadAsync(url, localPath, cancellationToken);
                    if (result.Success)
                    {
                        return DownloadOutcome.CreateSuccess(localPath);
                    }

                    if (!result.NotFound)
                    {
                        anyNotFound = false;
                        lastError = result.ErrorMessage ?? lastError;
                    }
                }
            }

            if (anyNotFound)
            {
                return DownloadOutcome.CreateNotFound();
            }

            if (attempt < _config.RetryCount)
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.RetryBackoffSeconds), cancellationToken);
            }
        }

        if (_verbose && !string.IsNullOrWhiteSpace(lastError))
        {
            Console.WriteLine(lastError);
        }

        return DownloadOutcome.Failure(lastError);
    }

    private async Task<DownloadOutcome> DownloadDailyToPoolAsync(
        string instrument,
        DateTimeOffset dayUtc,
        string fileName,
        bool refreshCache,
        bool verifyChecksum,
        int recentRefreshDays,
        CancellationToken cancellationToken)
    {
        var monthCandidates = new List<int>
        {
            dayUtc.Month - 1,
            dayUtc.Month
        }.Where(m => m is >= 0 and <= 12).Distinct().ToList();

        var cacheOnly = !refreshCache && recentRefreshDays <= 0;

        if (!ShouldRefresh(dayUtc, refreshCache, recentRefreshDays))
        {
            foreach (var month in monthCandidates)
            {
                var localPath = _pool.GetLocalPath(instrument, dayUtc.Year, month, dayUtc.Day, fileName);
                if (TryUseCache(localPath, verifyChecksum))
                {
                    return DownloadOutcome.FromCache(localPath);
                }
            }

            if (cacheOnly)
            {
                return DownloadOutcome.CreateNotFound();
            }
        }

        var anyNotFound = true;
        var lastError = string.Empty;

        for (var attempt = 1; attempt <= _config.RetryCount; attempt++)
        {
            anyNotFound = true;
            foreach (var month in monthCandidates)
            {
                var relativePath = $"{instrument}/{dayUtc:yyyy}/{month:00}/{dayUtc:dd}/{fileName}";
                var localPath = _pool.GetLocalPath(instrument, dayUtc.Year, month, dayUtc.Day, fileName);
                foreach (var baseUrl in _config.BaseUrls)
                {
                    var url = $"{baseUrl.TrimEnd('/')}/{relativePath}";
                    var result = await TryDownloadAsync(url, localPath, cancellationToken);
                    if (result.Success)
                    {
                        return DownloadOutcome.CreateSuccess(localPath);
                    }

                    if (!result.NotFound)
                    {
                        anyNotFound = false;
                        lastError = result.ErrorMessage ?? lastError;
                    }
                }
            }

            if (anyNotFound)
            {
                return DownloadOutcome.CreateNotFound();
            }

            if (attempt < _config.RetryCount)
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.RetryBackoffSeconds), cancellationToken);
            }
        }

        if (_verbose && !string.IsNullOrWhiteSpace(lastError))
        {
            Console.WriteLine(lastError);
        }

        return DownloadOutcome.Failure(lastError);
    }

    private async Task<DownloadResult> TryDownloadAsync(string url, string localPath, CancellationToken cancellationToken)
    {
        try
        {
            if (_verbose)
            {
                Console.WriteLine($"Downloading {url}");
            }

            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return DownloadResult.CreateNotFound();
            }

            response.EnsureSuccessStatusCode();
            var tempPath = localPath + ".tmp";
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true))
            {
                await input.CopyToAsync(output, cancellationToken);
            }

            var info = new FileInfo(tempPath);
            if (!info.Exists || info.Length == 0)
            {
                return DownloadResult.Failure("Downloaded file is empty.");
            }

            File.Move(tempPath, localPath, overwrite: true);
            TryWriteMeta(localPath);
            return DownloadResult.CreateSuccess();
        }
        // Real user cancellation (Ctrl+C) — propagate so the per-instrument loop can stop the run.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Everything else, including HttpClient TaskCanceledException for request-timeout —
        // treated as a transient failure. The retry loop in DownloadToPoolAsync handles it
        // (retries, eventually NotFound or persistent error). The hour ends up counted as
        // missing rather than aborting the entire instrument run.
        catch (Exception ex)
        {
            return DownloadResult.Failure(ex.Message);
        }
    }

    private async Task<ProbeResult> ProbeUrlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return ProbeResult.CreateNotFound();
            }

            if ((int)response.StatusCode >= 500)
            {
                return ProbeResult.Failure($"HTTP {(int)response.StatusCode}");
            }

            if (response.IsSuccessStatusCode)
            {
                return ProbeResult.CreateFound();
            }

            return ProbeResult.Failure($"HTTP {(int)response.StatusCode}");
        }
        // Real user cancellation (Ctrl+C) — propagate.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Everything else, including HttpClient request timeouts — treated as a probe failure
        // for this URL/baseUrl combination. The probe retry / fallback baseUrls handle it.
        catch (Exception ex)
        {
            return ProbeResult.Failure(ex.Message);
        }
    }

    private static IReadOnlyList<DateTimeOffset> GetProbeHours(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var set = new HashSet<DateTimeOffset>
        {
            startUtc.ToUniversalTime().TrimToHour(),
            endUtc.ToUniversalTime().TrimToHour(),
            DateTimeOffset.UtcNow.AddDays(-7).TrimToHour(),
            DateTimeOffset.UtcNow.AddDays(-30).TrimToHour(),
            new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero)
        };

        return set.OrderBy(t => t).ToList();
    }

    private static IReadOnlyList<DateTimeOffset> GetProbeDays(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var set = new HashSet<DateTimeOffset>
        {
            new DateTimeOffset(startUtc.ToUniversalTime().Date, TimeSpan.Zero),
            new DateTimeOffset(endUtc.ToUniversalTime().Date, TimeSpan.Zero),
            new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(-7).Date, TimeSpan.Zero),
            new DateTimeOffset(DateTimeOffset.UtcNow.AddDays(-30).Date, TimeSpan.Zero),
            new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero)
        };

        return set.OrderBy(t => t).ToList();
    }

    private static IReadOnlyList<int> GetMonthCandidates(int month)
    {
        return new List<int>
        {
            month - 1,
            month
        }.Where(m => m is >= 0 and <= 12).Distinct().ToList();
    }

    private static (List<int> Bids, List<int> Asks) ParseRawTickPrices(ReadOnlySpan<byte> data, int sampleSize)
    {
        var bids = new List<int>(sampleSize);
        var asks = new List<int>(sampleSize);
        for (var offset = 0; offset + TickRecordSize <= data.Length && bids.Count < sampleSize; offset += TickRecordSize)
        {
            var bid = BigEndian.ReadInt32(data, offset + 4);
            var ask = BigEndian.ReadInt32(data, offset + 8);
            if (bid > 0 && ask >= bid)
            {
                bids.Add(bid);
                asks.Add(ask);
            }
        }

        return (bids, asks);
    }

    private static int InferDigits(IReadOnlyList<int> bids, IReadOnlyList<int> asks)
    {
        var bestDigits = 5;
        var bestScore = double.MinValue;
        var secondBest = double.MinValue;

        for (var digits = 0; digits <= 8; digits++)
        {
            var scale = Math.Pow(10, digits);
            var medianBid = Median(bids.Select(v => v / scale));
            var spreads = asks.Zip(bids, (ask, bid) => (ask - bid) / scale).Where(v => v > 0).ToArray();
            if (spreads.Length == 0)
            {
                continue;
            }

            var medianSpread = Median(spreads);
            var ratio = medianSpread / Math.Max(medianBid, 1e-9);
            var score = 0.0;

            if (medianBid > 0.01 && medianBid < 100000)
            {
                score += 2;
            }

            if (medianSpread > 0 && medianSpread < 100)
            {
                score += 2;
            }

            if (ratio > 1e-8 && ratio < 5e-2)
            {
                score += 3;
            }

            if (digits is >= 2 and <= 5)
            {
                score += 1;
            }

            if (score > bestScore)
            {
                secondBest = bestScore;
                bestScore = score;
                bestDigits = digits;
            }
            else if (score > secondBest)
            {
                secondBest = score;
            }
        }

        if (bestScore - secondBest < 1)
        {
            return 5;
        }

        return bestDigits;
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(v => v).ToArray();
        if (ordered.Length == 0)
        {
            return 0;
        }

        var mid = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[mid - 1] + ordered[mid]) / 2
            : ordered[mid];
    }

    private static bool ShouldRefresh(DateTimeOffset fileTimeUtc, bool refreshCache, int recentRefreshDays)
    {
        if (refreshCache)
        {
            return true;
        }

        if (recentRefreshDays <= 0)
        {
            return false;
        }

        var threshold = DateTimeOffset.UtcNow.AddDays(-recentRefreshDays);
        return fileTimeUtc >= threshold;
    }

    private static bool TryUseCache(string localPath, bool verifyChecksum)
    {
        if (!verifyChecksum)
        {
            return DataPool.DataPool.HasValidFile(localPath);
        }

        if (DataPoolFileMeta.VerifyFile(localPath))
        {
            return true;
        }

        RemoveCorruptFile(localPath);
        return false;
    }

    private static void RemoveCorruptFile(string localPath)
    {
        if (File.Exists(localPath))
        {
            File.Delete(localPath);
        }

        DataPoolFileMeta.DeleteMeta(localPath);
    }

    private void TryWriteMeta(string localPath)
    {
        try
        {
            DataPoolFileMeta.Write(localPath);
        }
        catch (Exception ex)
        {
            if (_verbose)
            {
                Console.WriteLine($"Failed to write metadata for {localPath}: {ex.Message}");
            }
        }
    }

    private static async Task<IReadOnlyList<Tick>> ReadTicksAsync(string path, DateTimeOffset hourUtc, int digits, CancellationToken cancellationToken)
    {
        var compressed = await File.ReadAllBytesAsync(path, cancellationToken);
        return await Task.Run(() =>
        {
            var data = DecompressLzma(compressed);
            return ParseTicks(data, hourUtc, digits);
        }, cancellationToken);
    }

    private static async Task<IReadOnlyList<Bar>> ReadBarsAsync(string path, DateTimeOffset hourUtc, int digits, CancellationToken cancellationToken)
    {
        var compressed = await File.ReadAllBytesAsync(path, cancellationToken);
        return await Task.Run(() =>
        {
            var data = DecompressLzma(compressed);
            return ParseBars(data, hourUtc, digits);
        }, cancellationToken);
    }

    internal static IReadOnlyList<Tick> ParseTicks(ReadOnlySpan<byte> data, DateTimeOffset hourUtc, int digits)
    {
        var ticks = new List<Tick>(data.Length / TickRecordSize);
        var scale = Math.Pow(10, digits);

        for (var offset = 0; offset + TickRecordSize <= data.Length; offset += TickRecordSize)
        {
            var ms = BigEndian.ReadInt32(data, offset);
            var bid = BigEndian.ReadInt32(data, offset + 4);
            var ask = BigEndian.ReadInt32(data, offset + 8);
            var bidVol = BigEndian.ReadSingle(data, offset + 12);
            var askVol = BigEndian.ReadSingle(data, offset + 16);

            var time = hourUtc.AddMilliseconds(ms);
            ticks.Add(new Tick(time, bid / scale, ask / scale, bidVol, askVol));
        }

        return ticks;
    }

    internal static IReadOnlyList<Bar> ParseBars(ReadOnlySpan<byte> data, DateTimeOffset hourUtc, int digits)
    {
        var bars = new List<Bar>(data.Length / M1RecordSize);
        var scale = Math.Pow(10, digits);

        for (var offset = 0; offset + M1RecordSize <= data.Length; offset += M1RecordSize)
        {
            var seconds = BigEndian.ReadInt32(data, offset);
            var open = BigEndian.ReadInt32(data, offset + 4);
            var high = BigEndian.ReadInt32(data, offset + 8);
            var low = BigEndian.ReadInt32(data, offset + 12);
            var close = BigEndian.ReadInt32(data, offset + 16);
            var volume = BigEndian.ReadSingle(data, offset + 20);

            var time = hourUtc.AddSeconds(seconds);
            bars.Add(new Bar(
                time,
                open / scale,
                high / scale,
                low / scale,
                close / scale,
                (long)Math.Round(volume),
                0,
                0));
        }

        return bars;
    }

    private static long CountHoursInRange(DateTimeOffset dayStart, DateTimeOffset dayEnd, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        var effectiveStart = dayStart > rangeStart ? dayStart : rangeStart;
        var effectiveEnd = dayEnd < rangeEnd ? dayEnd : rangeEnd;
        if (effectiveEnd < effectiveStart)
        {
            return 0;
        }

        return TimeRangeUtils.EnumerateHours(effectiveStart, effectiveEnd).LongCount();
    }

    private static byte[] DecompressLzma(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        var decoder = new Decoder();
        var properties = new byte[5];
        if (input.Read(properties, 0, 5) != 5)
        {
            throw new InvalidDataException("Invalid LZMA properties.");
        }

        decoder.SetDecoderProperties(properties);

        long outSize = 0;
        for (var i = 0; i < 8; i++)
        {
            var b = input.ReadByte();
            if (b < 0)
            {
                throw new EndOfStreamException("Unexpected end of LZMA stream.");
            }

            outSize |= (long)(byte)b << (8 * i);
        }

        using var output = new MemoryStream();
        decoder.Code(input, output, input.Length - input.Position, outSize, null);
        return output.ToArray();
    }

    private sealed record DownloadOutcome(bool Success, bool NotFound, string? LocalPath, string? ErrorMessage)
    {
        public static DownloadOutcome CreateSuccess(string localPath) => new(true, false, localPath, null);

        public static DownloadOutcome FromCache(string localPath) => new(true, false, localPath, null);

        public static DownloadOutcome CreateNotFound() => new(false, true, null, null);

        public static DownloadOutcome Failure(string? error) => new(false, false, null, error);
    }

    private sealed record DownloadResult(bool Success, bool NotFound, string? ErrorMessage)
    {
        public static DownloadResult CreateSuccess() => new(true, false, null);

        public static DownloadResult CreateNotFound() => new(false, true, null);

        public static DownloadResult Failure(string? error) => new(false, false, error);
    }

    private sealed record ProbeResult(bool Found, bool NotFound, string? ErrorMessage)
    {
        public static ProbeResult CreateFound() => new(true, false, null);

        public static ProbeResult CreateNotFound() => new(false, true, null);

        public static ProbeResult Failure(string? error) => new(false, false, error);
    }
}

public enum SymbolProbeStatus
{
    Available,
    NotFound,
    TransientError
}

/// <summary>
/// Outcome of a single-hour availability probe. <c>Available</c> means
/// at least one of the (month-candidate × base-URL) combinations returned
/// 200; <c>NotFound</c> means every combination returned 404;
/// <c>TransientError</c> means at least one combination errored
/// (timeout, 5xx, network) without any 200 — caller should retry or skip.
/// </summary>
public enum HourProbeResult
{
    Available,
    NotFound,
    TransientError
}

public sealed record SymbolProbeResult(SymbolProbeStatus Status, string? ErrorMessage);

internal static class DateTimeOffsetExtensions
{
    public static DateTimeOffset TrimToHour(this DateTimeOffset value)
    {
        return new DateTimeOffset(value.Year, value.Month, value.Day, value.Hour, 0, 0, TimeSpan.Zero);
    }
}
