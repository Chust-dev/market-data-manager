namespace HistoricalData.Export;

/// <summary>
/// How per-tick (or per-M1) spread samples are reduced to the single
/// <see cref="Models.Bar.Spread"/> value written into exports. Selected
/// via the `--spread-method` flag on <c>export bars</c>; default
/// <see cref="Last"/> preserves the historical behaviour.
/// </summary>
public enum SpreadMethod
{
    /// <summary>The spread of the most recently added sample (last tick of
    /// the minute, or last M1 bar of the higher-TF bucket).</summary>
    Last,

    /// <summary>Smallest spread observed in the bucket.</summary>
    Min,

    /// <summary>Arithmetic mean of all samples, rounded to integer points.</summary>
    Mean,

    /// <summary>Median sample. For an even count, the lower of the two
    /// middle values (no interpolation — keeps the result an integer
    /// point count without bias).</summary>
    Median,
}

/// <summary>
/// Per-bucket accumulator that absorbs spread samples and produces a
/// single point-spread value. Allocation-free for Last/Min/Mean; Median
/// is the only variant that allocates (a <see cref="List{Int32}"/> sized
/// by sample count). One instance per bucket — not thread-safe.
/// </summary>
internal struct SpreadAccumulator
{
    private readonly SpreadMethod _method;
    private int _last;
    private int _min;
    private long _sum;
    private int _count;
    private List<int>? _samples;

    public SpreadAccumulator(SpreadMethod method)
    {
        _method = method;
        _last = 0;
        _min = int.MaxValue;
        _sum = 0;
        _count = 0;
        _samples = null;
    }

    public void Add(int sample)
    {
        _last = sample;
        if (sample < _min) _min = sample;
        _sum += sample;
        _count++;
        if (_method == SpreadMethod.Median)
        {
            _samples ??= new List<int>();
            _samples.Add(sample);
        }
    }

    public int Build()
    {
        if (_count == 0) return 0;
        return _method switch
        {
            SpreadMethod.Last => _last,
            SpreadMethod.Min => _min,
            SpreadMethod.Mean => (int)(_sum / _count),
            SpreadMethod.Median => Median(_samples!),
            _ => _last,
        };
    }

    private static int Median(List<int> samples)
    {
        // Sort in place — accumulator is single-use per bucket so this is safe.
        samples.Sort();
        return samples[(samples.Count - 1) / 2];
    }
}
