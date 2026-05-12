using HistoricalData.Export;
using HistoricalData.Models;

namespace HistoricalData.Tests;

/// <summary>
/// Behaviour tests for <see cref="SpreadAccumulator"/> and the
/// integration of <see cref="SpreadMethod"/> through
/// <see cref="BarAggregator"/> and <see cref="BarResampler"/>.
/// </summary>
public sealed class SpreadAggregationTests
{
    // -- SpreadAccumulator: direct unit tests --------------------------------

    [Fact]
    public void Accumulator_Empty_ReturnsZero_ForAllMethods()
    {
        foreach (var method in new[] { SpreadMethod.Last, SpreadMethod.Min, SpreadMethod.Mean, SpreadMethod.Median })
        {
            var acc = new SpreadAccumulator(method);
            Assert.False(acc.HasSamples);
            Assert.Equal(0, acc.Get());
        }
    }

    [Fact]
    public void Accumulator_SingleSample_AllMethodsReturnSameValue()
    {
        foreach (var method in new[] { SpreadMethod.Last, SpreadMethod.Min, SpreadMethod.Mean, SpreadMethod.Median })
        {
            var acc = new SpreadAccumulator(method);
            acc.Add(42);
            Assert.True(acc.HasSamples);
            Assert.Equal(42, acc.Get());
        }
    }

    [Fact]
    public void Accumulator_Last_TracksMostRecentlyAdded()
    {
        var acc = new SpreadAccumulator(SpreadMethod.Last);
        acc.Add(1);
        acc.Add(3);
        acc.Add(5);
        acc.Add(7);
        acc.Add(9);
        Assert.Equal(9, acc.Get());
    }

    [Fact]
    public void Accumulator_Min_TracksMinimum()
    {
        var acc = new SpreadAccumulator(SpreadMethod.Min);
        acc.Add(5);
        acc.Add(3);
        acc.Add(7);
        acc.Add(1);
        acc.Add(9);
        Assert.Equal(1, acc.Get());
    }

    [Fact]
    public void Accumulator_Mean_RoundsHalfAwayFromZero()
    {
        // 24 ticks, sum 47 → 47/24 = 1.958… rounds to 2, not floor=1.
        var acc = new SpreadAccumulator(SpreadMethod.Mean);
        for (var i = 0; i < 21; i++) acc.Add(2);
        acc.Add(1); acc.Add(1); acc.Add(3);  // total = 21*2 + 1 + 1 + 3 = 47, count = 24
        Assert.Equal(2, acc.Get());
    }

    [Fact]
    public void Accumulator_Mean_OutlierSensitivity()
    {
        // 1, 2, 3, 100 → mean = 106/4 = 26.5 → rounds away-from-zero to 27.
        var acc = new SpreadAccumulator(SpreadMethod.Mean);
        acc.Add(1); acc.Add(2); acc.Add(3); acc.Add(100);
        Assert.Equal(27, acc.Get());
    }

    [Fact]
    public void Accumulator_Median_OutlierRobust()
    {
        // 1, 2, 3, 100 → median is between 2 and 3 → midpoint 2.5 → rounds to 3.
        var acc = new SpreadAccumulator(SpreadMethod.Median);
        acc.Add(1); acc.Add(2); acc.Add(3); acc.Add(100);
        Assert.Equal(3, acc.Get());
    }

    [Fact]
    public void Accumulator_Median_OddCount_ReturnsMiddleValue()
    {
        var acc = new SpreadAccumulator(SpreadMethod.Median);
        acc.Add(1); acc.Add(3); acc.Add(5); acc.Add(7); acc.Add(9);
        Assert.Equal(5, acc.Get());
    }

    [Fact]
    public void Accumulator_Median_EvenCount_RoundsMidpoint()
    {
        // 2, 4, 6, 8 → midpoint of 4 and 6 = 5.
        var acc = new SpreadAccumulator(SpreadMethod.Median);
        acc.Add(2); acc.Add(4); acc.Add(6); acc.Add(8);
        Assert.Equal(5, acc.Get());
    }

    [Fact]
    public void Accumulator_NegativeSpreads_PassThrough()
    {
        // Crossed quotes: 1, 1, -2, 1, 1. Each method should surface its own view.
        var last = new SpreadAccumulator(SpreadMethod.Last);
        var min = new SpreadAccumulator(SpreadMethod.Min);
        var mean = new SpreadAccumulator(SpreadMethod.Mean);
        var median = new SpreadAccumulator(SpreadMethod.Median);

        foreach (var s in new[] { 1, 1, -2, 1, 1 })
        {
            last.Add(s);
            min.Add(s);
            mean.Add(s);
            median.Add(s);
        }

        Assert.Equal(1, last.Get());      // last sample
        Assert.Equal(-2, min.Get());      // crossed quote surfaces
        Assert.Equal(0, mean.Get());      // 2/5 = 0.4 → rounds to 0
        Assert.Equal(1, median.Get());    // sorted -2,1,1,1,1 → middle = 1
    }

    // -- BarBuilder regression: default mode = last-tick semantics -----------

    [Fact]
    public void BarAggregator_DefaultSpreadMethod_MatchesLegacyLastTickBehaviour()
    {
        // Pins the legacy guarantee: with --spread-method last (default), the M1
        // bar's Spread is the last tick's (ask - bid) scaled to digits — byte-
        // identical to the pre-#43 code path.
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);  // Monday
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator(
            "m1", 5, BrokerOffset.Fixed(TimeSpan.Zero), filterWeekends: false, start, end);

        // 3 ticks with different spreads: 0.00001, 0.00003, 0.00007 → 1, 3, 7 in points.
        aggregator.AddTick(new Tick(start.AddSeconds(5),  Bid: 1.10000, Ask: 1.10001, 0f, 0f));
        aggregator.AddTick(new Tick(start.AddSeconds(20), Bid: 1.10000, Ask: 1.10003, 0f, 0f));
        aggregator.AddTick(new Tick(start.AddSeconds(40), Bid: 1.10000, Ask: 1.10007, 0f, 0f));

        var bars = aggregator.GetBars();
        Assert.Single(bars);
        Assert.Equal(7, bars[0].Spread);  // last tick's spread in points
    }

    [Fact]
    public void BarAggregator_MinSpreadMethod_TakesMinimumAcrossTicks()
    {
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator(
            "m1", 5, BrokerOffset.Fixed(TimeSpan.Zero), filterWeekends: false, start, end,
            spreadMethod: SpreadMethod.Min);

        aggregator.AddTick(new Tick(start.AddSeconds(5),  Bid: 1.10000, Ask: 1.10007, 0f, 0f));
        aggregator.AddTick(new Tick(start.AddSeconds(20), Bid: 1.10000, Ask: 1.10001, 0f, 0f));  // tightest
        aggregator.AddTick(new Tick(start.AddSeconds(40), Bid: 1.10000, Ask: 1.10005, 0f, 0f));

        var bars = aggregator.GetBars();
        Assert.Equal(1, bars[0].Spread);
    }

    [Fact]
    public void BarAggregator_MeanSpreadMethod_AveragesAcrossTicks()
    {
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator(
            "m1", 5, BrokerOffset.Fixed(TimeSpan.Zero), filterWeekends: false, start, end,
            spreadMethod: SpreadMethod.Mean);

        // Spreads 1, 3, 5 → mean = 3.
        aggregator.AddTick(new Tick(start.AddSeconds(5),  Bid: 1.10000, Ask: 1.10001, 0f, 0f));
        aggregator.AddTick(new Tick(start.AddSeconds(20), Bid: 1.10000, Ask: 1.10003, 0f, 0f));
        aggregator.AddTick(new Tick(start.AddSeconds(40), Bid: 1.10000, Ask: 1.10005, 0f, 0f));

        Assert.Equal(3, aggregator.GetBars()[0].Spread);
    }

    [Fact]
    public void BarAggregator_FallbackOnly_UsesFallbackSpreadDirectly()
    {
        // No ticks, just a fallback bar — spread is the fallback's value regardless of method.
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator(
            "m1", 5, BrokerOffset.Fixed(TimeSpan.Zero), filterWeekends: false, start, end,
            spreadMethod: SpreadMethod.Mean);

        aggregator.AddBar(new Bar(start, 1.10, 1.11, 1.09, 1.105, Volume: 100, Spread: 5, RealVolume: 100));

        Assert.Equal(5, aggregator.GetBars()[0].Spread);
    }

    [Fact]
    public void BarAggregator_TicksAndFallback_WidensViaMath_Max()
    {
        // Q3=3a regression: with --allow-fallback-overlap, the fallback bar's Spread
        // can only widen the tick-derived result (Math.Max), never narrow it.
        // Ticks → mean = 1; fallback's Spread = 5 → final = max(1, 5) = 5.
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator(
            "m1", 5, BrokerOffset.Fixed(TimeSpan.Zero), filterWeekends: false, start, end,
            skipFallbackIfTicked: false,
            spreadMethod: SpreadMethod.Mean);

        aggregator.AddTick(new Tick(start.AddSeconds(5),  Bid: 1.10000, Ask: 1.10001, 0f, 0f));
        aggregator.AddTick(new Tick(start.AddSeconds(20), Bid: 1.10000, Ask: 1.10001, 0f, 0f));
        aggregator.AddBar(new Bar(start, 1.10, 1.11, 1.09, 1.105, Volume: 100, Spread: 5, RealVolume: 100));

        Assert.Equal(5, aggregator.GetBars()[0].Spread);
    }

    // -- BarResampler: method propagates through M1 → higher-TF --------------

    [Fact]
    public void BarResampler_MeanSpreadMethod_AveragesAcrossM1Bars()
    {
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<Bar>
        {
            new(start,                   1.1, 1.2, 1.0, 1.15, 10, Spread: 2,  RealVolume: 3),
            new(start.AddMinutes(1),     1.15,1.25,1.12,1.22, 11, Spread: 4,  RealVolume: 3),
            new(start.AddMinutes(2),     1.22,1.3, 1.18,1.28, 12, Spread: 6,  RealVolume: 4),
        };

        // 3-bar bucket, mean of 2,4,6 = 4.
        var resampled = BarResampler.Resample(bars, 5, SpreadMethod.Mean);
        Assert.Single(resampled);
        Assert.Equal(4, resampled[0].Spread);
    }

    [Fact]
    public void BarResampler_LastSpreadMethod_TakesLastM1Bar()
    {
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<Bar>
        {
            new(start,                   1.1, 1.2, 1.0, 1.15, 10, Spread: 2,  RealVolume: 3),
            new(start.AddMinutes(1),     1.15,1.25,1.12,1.22, 11, Spread: 9,  RealVolume: 3),
            new(start.AddMinutes(2),     1.22,1.3, 1.18,1.28, 12, Spread: 3,  RealVolume: 4),
        };

        // Default = Last → bucket spread is the LAST M1 bar's spread (3).
        var resampled = BarResampler.Resample(bars, 5);
        Assert.Single(resampled);
        Assert.Equal(3, resampled[0].Spread);
    }

    [Fact]
    public void BarResampler_MinSpreadMethod_TakesMinimumAcrossM1Bars()
    {
        var start = new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<Bar>
        {
            new(start,                   1.1, 1.2, 1.0, 1.15, 10, Spread: 7,  RealVolume: 3),
            new(start.AddMinutes(1),     1.15,1.25,1.12,1.22, 11, Spread: 2,  RealVolume: 3),
            new(start.AddMinutes(2),     1.22,1.3, 1.18,1.28, 12, Spread: 5,  RealVolume: 4),
        };

        var resampled = BarResampler.Resample(bars, 5, SpreadMethod.Min);
        Assert.Equal(2, resampled[0].Spread);
    }
}
