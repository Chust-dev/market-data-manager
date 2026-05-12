namespace HistoricalData.Export;

/// <summary>
/// Strategy for collapsing the spreads of N underlying samples into a
/// single integer for a bar's <c>Spread</c> field. The "underlying samples"
/// are per-tick spreads at the aggregator stage and per-M1-bar spreads at
/// the resampler stage.
///
/// Default is <see cref="Last"/>, matching the legacy hardcoded behaviour
/// at the aggregator (last tick's spread wins) and giving consistent
/// last-sample semantics at the resampler too (last M1 bar's spread).
/// </summary>
public enum SpreadMethod
{
    /// <summary>Spread of the final sample. Default; preserves legacy
    /// per-tick behaviour byte-for-byte at the M1 stage.</summary>
    Last,

    /// <summary>Minimum spread across all samples. Best-case fill;
    /// surfaces crossed quotes (negative spreads) when they occur.</summary>
    Min,

    /// <summary>Arithmetic mean of all samples, rounded half-away-from-zero
    /// to the nearest integer. At the resampler stage this is
    /// mean-of-means (each M1 weighted equally), not the true global
    /// mean across underlying ticks — an approximation that matters only
    /// when minute-to-minute tick counts vary widely.</summary>
    Mean,

    /// <summary>Median of all samples; for even sample counts the mean of
    /// the two middle samples, rounded half-away-from-zero. Outlier-robust
    /// at the cost of a per-bucket allocation.</summary>
    Median,
}

/// <summary>
/// Streaming spread aggregator used by <c>BarBuilder</c> (per-tick) and
/// <c>BarResampler</c> (per-M1-bar). Allocation-free for
/// <see cref="SpreadMethod.Last"/>, <see cref="SpreadMethod.Min"/>, and
/// <see cref="SpreadMethod.Mean"/>; allocates a single <c>List&lt;int&gt;</c>
/// per instance only when <see cref="SpreadMethod.Median"/> is selected.
///
/// Negative spreads (transient bid &gt; ask in the Dukascopy tick stream)
/// are preserved as signed samples — useful diagnostic signal in
/// <see cref="SpreadMethod.Min"/> mode, and the right behaviour in mean /
/// median modes too (lose the inversion silently otherwise).
///
/// The struct must be initialised via the explicit constructor for
/// <see cref="SpreadMethod.Median"/> to work; a default-constructed
/// instance behaves as <see cref="SpreadMethod.Last"/> with no median list.
/// </summary>
public struct SpreadAccumulator
{
    private readonly SpreadMethod _method;
    private int _last;
    private int _min;
    private long _sum;
    private long _count;
    private List<int>? _samples;
    private bool _hasAny;

    public SpreadAccumulator(SpreadMethod method)
    {
        _method = method;
        _last = 0;
        _min = 0;
        _sum = 0;
        _count = 0;
        _samples = method == SpreadMethod.Median ? new List<int>(64) : null;
        _hasAny = false;
    }

    public bool HasSamples => _hasAny;

    public void Add(int spread)
    {
        if (!_hasAny || spread < _min)
        {
            _min = spread;
        }
        _last = spread;
        _sum += spread;
        _count++;
        _samples?.Add(spread);
        _hasAny = true;
    }

    public int Get()
    {
        if (!_hasAny) return 0;
        return _method switch
        {
            SpreadMethod.Last => _last,
            SpreadMethod.Min => _min,
            SpreadMethod.Mean => RoundToInt((double)_sum / _count),
            SpreadMethod.Median => ComputeMedian(),
            _ => _last,
        };
    }

    private int ComputeMedian()
    {
        var n = _samples!.Count;
        if (n == 0) return 0;
        var sorted = _samples.ToArray();
        Array.Sort(sorted);
        if ((n & 1) == 1)
        {
            return sorted[n / 2];
        }
        // Even count: midpoint of the two centre samples, rounded.
        return RoundToInt((sorted[n / 2 - 1] + sorted[n / 2]) / 2.0);
    }

    private static int RoundToInt(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);
}
