namespace HistoricalData.Manage;

/// <summary>
/// Finds the earliest UTC hour Dukascopy has data for a symbol, by
/// binary-searching .bi5 URL availability. Pure read-only — no files
/// are downloaded; only HTTP HEAD-equivalent probes are issued. Lives
/// in the Manage pillar alongside <see cref="PoolAuditor"/>.
///
/// Algorithm:
///   1. Sanity probe a recent date (today − 7d). NotFound → symbol
///      isn't on Dukascopy at all (delisted / wrong ticker).
///   2. Binary search [since, today − 7d] by day, probing 3 hours per
///      midpoint to tolerate weekend / holiday gaps. Converges in
///      O(log range) steps — ~26 iterations across a 25-year span.
///   3. Return the earliest day where any probe came back Available,
///      represented as midnight UTC. Day-level precision is sufficient
///      for `cache update` / `cache catchup` lower bounds.
/// </summary>
public sealed class CacheDiscoverer
{
    /// <summary>
    /// Hours of the day to probe per binary-search step. Spread across
    /// the active US/EU sessions so a Wednesday probe almost always
    /// catches at least one hour with data, even for thinly-traded pairs.
    /// </summary>
    private static readonly int[] ProbeHoursOfDay = { 10, 14, 18 };

    private readonly DukascopyClient _client;

    public CacheDiscoverer(DukascopyClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<DiscoveryResult> DiscoverAsync(
        string instrument,
        DateTimeOffset since,
        DateTimeOffset until,
        CancellationToken cancellationToken = default)
    {
        var probes = 0;

        // Step 1: sanity check. If the symbol has no data 7 days ago,
        // it's either delisted or never existed — skip the binary search.
        var sanityDay = new DateTimeOffset(until.AddDays(-7).UtcDateTime.Date, TimeSpan.Zero);
        var sanity = await ProbeDayAsync(instrument, sanityDay, cancellationToken);
        probes += sanity.Probes;
        if (!sanity.HasData)
        {
            return new DiscoveryResult(
                Earliest: null,
                Probes: probes,
                Note: sanity.HadTransientError ? "transient error" : "not available");
        }

        // Step 2: binary search [since.Date, sanityDay] for the earliest
        // day with data. The invariant is `earliest ∈ [low, high]` until
        // the loop closes. Seed `bestKnown` with the sanityDay we already
        // confirmed has data — this protects the empty-range edge case
        // (since > sanityDay) and gives a sane fallback if every probe
        // earlier than sanityDay misses (very thinly-traded symbols).
        var low = new DateTimeOffset(since.UtcDateTime.Date, TimeSpan.Zero);
        var high = sanityDay;
        DateTimeOffset? bestKnown = sanityDay;

        while (low <= high)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Integer midpoint by day count, using `low + (high - low) / 2`
            // to avoid overflow on huge ranges (irrelevant here, but cheap insurance).
            var spanDays = (long)(high - low).TotalDays;
            var mid = low.AddDays(spanDays / 2);

            var probe = await ProbeDayAsync(instrument, mid, cancellationToken);
            probes += probe.Probes;

            if (probe.HasData)
            {
                bestKnown = mid;
                high = mid.AddDays(-1);
            }
            else
            {
                low = mid.AddDays(1);
            }
        }

        // bestKnown is the smallest day during the search where any of
        // the probed hours returned Available. Always non-null here
        // because step 1 confirmed the symbol has at least the sanity day.
        return new DiscoveryResult(
            Earliest: bestKnown,
            Probes: probes,
            Note: null);
    }

    /// <summary>
    /// Probe several hours of one UTC day. Returns Available as soon as
    /// any probe finds the .bi5; returns NotFound only if every probe
    /// returned 404; flags a transient-error path for diagnostics.
    /// </summary>
    private async Task<DayProbeOutcome> ProbeDayAsync(
        string instrument,
        DateTimeOffset day,
        CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(day.UtcDateTime.Date, TimeSpan.Zero);
        var sawTransient = false;
        var probes = 0;

        foreach (var h in ProbeHoursOfDay)
        {
            ct.ThrowIfCancellationRequested();
            probes++;
            var hourTime = dayStart.AddHours(h);
            var result = await _client.ProbeHourAvailableAsync(instrument, hourTime, ct);
            if (result == HourProbeResult.Available)
            {
                return new DayProbeOutcome(HasData: true, HadTransientError: false, Probes: probes);
            }
            if (result == HourProbeResult.TransientError)
            {
                sawTransient = true;
            }
        }

        return new DayProbeOutcome(HasData: false, HadTransientError: sawTransient, Probes: probes);
    }

    private sealed record DayProbeOutcome(bool HasData, bool HadTransientError, int Probes);
}

/// <summary>
/// Per-symbol outcome of a discovery run. <see cref="Earliest"/> is the
/// midnight-UTC of the earliest day with confirmed data, or null if the
/// symbol is unavailable on the source. <see cref="Probes"/> is purely
/// diagnostic. <see cref="Note"/> carries human-readable status for
/// the unavailable / transient cases.
/// </summary>
public sealed record DiscoveryResult(
    DateTimeOffset? Earliest,
    int Probes,
    string? Note);
