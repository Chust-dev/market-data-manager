using HistoricalData.Models;
using HistoricalData.Utils;

namespace HistoricalData.Export;

public static class BarResampler
{
    public static IReadOnlyList<Bar> Resample(IReadOnlyList<Bar> bars, TimeframeInfo timeframe)
    {
        if (timeframe.Minutes <= 1 || bars.Count == 0)
        {
            return bars;
        }

        var result = new List<Bar>();
        Bar? current = null;
        DateTimeOffset currentBucket = default;

        var orderedBars = bars;
        if (!IsSorted(orderedBars))
        {
            orderedBars = orderedBars.OrderBy(b => b.Time).ToList();
        }

        foreach (var bar in orderedBars)
        {
            var bucket = GetBucketStart(bar.Time, timeframe);

            if (current is null || bucket != currentBucket)
            {
                if (current is not null)
                {
                    result.Add(current);
                }

                currentBucket = bucket;
                current = new Bar(
                    bucket,
                    bar.Open,
                    bar.High,
                    bar.Low,
                    bar.Close,
                    bar.Volume,
                    bar.Spread,
                    bar.RealVolume);
                continue;
            }

            current = current with
            {
                High = Math.Max(current.High, bar.High),
                Low = Math.Min(current.Low, bar.Low),
                Close = bar.Close,
                Volume = current.Volume + bar.Volume,
                Spread = Math.Max(current.Spread, bar.Spread),
                RealVolume = current.RealVolume + bar.RealVolume
            };
        }

        if (current is not null)
        {
            result.Add(current);
        }

        return result;
    }

    public static IReadOnlyList<Bar> Resample(IReadOnlyList<Bar> bars, int timeframeMinutes)
    {
        return Resample(bars, new TimeframeInfo($"m{timeframeMinutes}", timeframeMinutes, TimeframeKind.FixedMinutes));
    }

    private static DateTimeOffset GetBucketStart(DateTimeOffset time, TimeframeInfo timeframe)
    {
        return timeframe.Kind switch
        {
            TimeframeKind.Day => new DateTimeOffset(time.Year, time.Month, time.Day, 0, 0, 0, time.Offset),
            TimeframeKind.Week => GetWeekStart(time),
            TimeframeKind.Month => new DateTimeOffset(time.Year, time.Month, 1, 0, 0, 0, time.Offset),
            _ => AlignToFixedMinutes(time, timeframe.Minutes)
        };
    }

    private static DateTimeOffset AlignToFixedMinutes(DateTimeOffset time, int timeframeMinutes)
    {
        var offsetMinutes = (long)time.Offset.TotalMinutes;
        var utcMinutes = time.ToUnixTimeSeconds() / 60;
        var localMinutes = utcMinutes + offsetMinutes;
        var bucketLocalMinutes = (localMinutes / timeframeMinutes) * timeframeMinutes;
        var bucketUtcMinutes = bucketLocalMinutes - offsetMinutes;
        return DateTimeOffset.FromUnixTimeSeconds(bucketUtcMinutes * 60).ToOffset(time.Offset);
    }

    private static DateTimeOffset GetWeekStart(DateTimeOffset time)
    {
        var localDate = time.Date;
        var delta = ((int)localDate.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var weekStart = localDate.AddDays(-delta);
        return new DateTimeOffset(weekStart.Year, weekStart.Month, weekStart.Day, 0, 0, 0, time.Offset);
    }

    private static bool IsSorted(IReadOnlyList<Bar> bars)
    {
        for (var i = 1; i < bars.Count; i++)
        {
            if (bars[i - 1].Time > bars[i].Time)
            {
                return false;
            }
        }

        return true;
    }
}
