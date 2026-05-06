using System.Globalization;

namespace HistoricalData.Utils;

public static class DateTimeParser
{
    public static DateTimeOffset TryParse(string? value, DateTimeOffset fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }
}
