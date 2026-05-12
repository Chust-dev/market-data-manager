namespace HistoricalData.Export;

/// <summary>
/// UTC → broker-server-local offset, resolved per UTC instant so brokers
/// that observe daylight-saving time produce correctly-stamped bars and
/// ticks across DST transitions.
///
/// Two flavours, both built via static factories on this type:
///   <see cref="Fixed"/>      — a constant offset, identical to the old
///                              <c>--offset</c> behaviour.
///   <see cref="IcMarkets"/>  — IC Markets / IC Trading server time:
///                              GMT+2 winter, GMT+3 summer, US DST schedule.
///
/// The CLI surfaces this as either <c>--offset +HH:MM</c> (literal) or
/// <c>--broker NAME</c> (preset). Mutually exclusive: pick one or neither
/// (default: UTC).
/// </summary>
public sealed class BrokerOffset
{
    private readonly Func<DateTimeOffset, TimeSpan> _resolve;

    public string Name { get; }

    private BrokerOffset(string name, Func<DateTimeOffset, TimeSpan> resolve)
    {
        Name = name;
        _resolve = resolve;
    }

    public TimeSpan At(DateTimeOffset utc) => _resolve(utc);

    public static BrokerOffset Fixed(TimeSpan offset) =>
        new(offset == TimeSpan.Zero ? "UTC" : $"fixed {FormatOffset(offset)}", _ => offset);

    /// <summary>
    /// IC Markets / IC Trading: GMT+2 in winter, GMT+3 in summer. The server
    /// clock follows the **US** DST schedule (2nd Sunday March 02:00 ET →
    /// 1st Sunday November 02:00 ET) so wall-clock rollover stays at 17:00
    /// New York time year-round.
    /// </summary>
    public static readonly BrokerOffset IcMarkets =
        new("ic-markets", utc => IsUsDstActive(utc.UtcDateTime)
            ? TimeSpan.FromHours(3)
            : TimeSpan.FromHours(2));

    /// <summary>
    /// Resolve a <c>--broker NAME</c> value to a profile. Returns null when
    /// the name isn't recognised so the caller can surface a friendly error
    /// listing <see cref="KnownNames"/>.
    /// </summary>
    public static BrokerOffset? TryResolve(string name) =>
        name.Equals("ic-markets", StringComparison.OrdinalIgnoreCase) ? IcMarkets : null;

    public static IReadOnlyList<string> KnownNames { get; } = new[] { "ic-markets" };

    // US DST rule, explicit because TimeZoneInfo Windows/IANA name
    // resolution is platform-fragile (different IDs on Windows vs Linux,
    // and ICU dependency on .NET 8+). Behaviour is anchored by tests in
    // BrokerOffsetTests.
    private static bool IsUsDstActive(DateTime utc)
    {
        var year = utc.Year;
        // 2nd Sunday of March at 02:00 ET (EST = UTC-5) → 07:00 UTC.
        var dstStartUtc = SecondSundayOfMarch(year).AddHours(7);
        // 1st Sunday of November at 02:00 ET (EDT = UTC-4) → 06:00 UTC.
        var dstEndUtc = FirstSundayOfNovember(year).AddHours(6);
        return utc >= dstStartUtc && utc < dstEndUtc;
    }

    private static DateTime SecondSundayOfMarch(int year)
    {
        var march1 = new DateTime(year, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var daysToFirstSunday = ((int)DayOfWeek.Sunday - (int)march1.DayOfWeek + 7) % 7;
        return march1.AddDays(daysToFirstSunday + 7);
    }

    private static DateTime FirstSundayOfNovember(int year)
    {
        var nov1 = new DateTime(year, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var daysToFirstSunday = ((int)DayOfWeek.Sunday - (int)nov1.DayOfWeek + 7) % 7;
        return nov1.AddDays(daysToFirstSunday);
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset.Ticks >= 0 ? "+" : "-";
        var abs = offset.Duration();
        return $"{sign}{abs.Hours:D2}:{abs.Minutes:D2}";
    }
}
