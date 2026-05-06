namespace HistoricalData.Utils;

public static class TimeframeUtils
{
    public const string SupportedPresetTimeframes = "m1, m5, m15, m30, h1, h4, h6, d1, w1, mn1";
    public const string SupportedTimeframeHint = "Use m1, m5, m15, m30, h1, h4, h6, d1, w1, mn1, or m<minutes>.";

    private static readonly Dictionary<string, int> FixedMinuteMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m1"] = 1,
        ["m5"] = 5,
        ["m15"] = 15,
        ["m30"] = 30,
        ["h1"] = 60,
        ["h4"] = 240,
        ["h6"] = 360
    };

    public static bool TryGetMinutes(string timeframe, out int minutes)
    {
        if (!TryParse(timeframe, out var info))
        {
            minutes = 0;
            return false;
        }

        minutes = info.Minutes;
        return true;
    }

    public static bool TryParse(string timeframe, out TimeframeInfo info)
    {
        info = default;
        if (string.IsNullOrWhiteSpace(timeframe))
        {
            return false;
        }

        var token = timeframe.Trim();
        if (FixedMinuteMap.TryGetValue(token, out var minutes))
        {
            info = new TimeframeInfo(token.ToLowerInvariant(), minutes, TimeframeKind.FixedMinutes);
            return true;
        }

        if (token.Equals("d1", StringComparison.OrdinalIgnoreCase))
        {
            info = new TimeframeInfo("d1", 1440, TimeframeKind.Day);
            return true;
        }

        if (token.Equals("w1", StringComparison.OrdinalIgnoreCase))
        {
            info = new TimeframeInfo("w1", 10080, TimeframeKind.Week);
            return true;
        }

        if (token.Equals("mn1", StringComparison.OrdinalIgnoreCase))
        {
            info = new TimeframeInfo("mn1", 43200, TimeframeKind.Month);
            return true;
        }

        if (token.StartsWith("m", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(token.AsSpan(1), out minutes)
            && minutes > 0)
        {
            info = new TimeframeInfo($"m{minutes}", minutes, TimeframeKind.FixedMinutes);
            return true;
        }

        return false;
    }
}

public enum TimeframeKind
{
    FixedMinutes,
    Day,
    Week,
    Month
}

public readonly record struct TimeframeInfo(string Token, int Minutes, TimeframeKind Kind);
