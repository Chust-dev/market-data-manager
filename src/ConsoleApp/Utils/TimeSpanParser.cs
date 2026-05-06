using System.Globalization;

namespace HistoricalData.Utils;

public static class TimeSpanParser
{
    public static TimeSpan TryParse(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        if (value.StartsWith("+", StringComparison.Ordinal))
        {
            if (TimeSpan.TryParse(value[1..], CultureInfo.InvariantCulture, out parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }
}
