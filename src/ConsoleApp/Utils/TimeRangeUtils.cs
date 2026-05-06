namespace HistoricalData.Utils;

public static class TimeRangeUtils
{
    public static IEnumerable<DateTimeOffset> EnumerateHours(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var current = new DateTimeOffset(
            startUtc.UtcDateTime.Year,
            startUtc.UtcDateTime.Month,
            startUtc.UtcDateTime.Day,
            startUtc.UtcDateTime.Hour,
            0,
            0,
            TimeSpan.Zero);

        while (current <= endUtc)
        {
            yield return current;
            current = current.AddHours(1);
        }
    }

    public static IEnumerable<DateTimeOffset> EnumerateDays(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var current = new DateTimeOffset(
            startUtc.UtcDateTime.Year,
            startUtc.UtcDateTime.Month,
            startUtc.UtcDateTime.Day,
            0,
            0,
            0,
            TimeSpan.Zero);

        var endDay = new DateTimeOffset(
            endUtc.UtcDateTime.Year,
            endUtc.UtcDateTime.Month,
            endUtc.UtcDateTime.Day,
            0,
            0,
            0,
            TimeSpan.Zero);

        while (current <= endDay)
        {
            yield return current;
            current = current.AddDays(1);
        }
    }
}
