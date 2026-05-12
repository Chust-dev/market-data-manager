using HistoricalData.Export;

namespace HistoricalData.Tests;

/// <summary>
/// Behaviour tests for <see cref="BrokerOffset"/>, especially the
/// IC Markets DST rule (2nd Sunday March 02:00 ET → 1st Sunday November
/// 02:00 ET) which is hand-coded rather than delegating to
/// <c>TimeZoneInfo</c>. Tests anchor both the +2/+3 base values and the
/// exact UTC moments where the switchover happens.
/// </summary>
public sealed class BrokerOffsetTests
{
    [Fact]
    public void Fixed_AlwaysReturnsConstant()
    {
        var offset = BrokerOffset.Fixed(TimeSpan.FromHours(2));

        Assert.Equal(TimeSpan.FromHours(2),
            offset.At(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(TimeSpan.FromHours(2),
            offset.At(new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Fixed_Zero_HasUtcName()
    {
        Assert.Equal("UTC", BrokerOffset.Fixed(TimeSpan.Zero).Name);
    }

    [Fact]
    public void IcMarkets_WinterDate_ReturnsPlus2()
    {
        // Early February 2026 — well inside winter time.
        var winter = new DateTimeOffset(2026, 2, 4, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(2), BrokerOffset.IcMarkets.At(winter));
    }

    [Fact]
    public void IcMarkets_SummerDate_ReturnsPlus3()
    {
        // Mid-July 2026 — well inside DST.
        var summer = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromHours(3), BrokerOffset.IcMarkets.At(summer));
    }

    [Fact]
    public void IcMarkets_AcrossSpringDstTransition_2026()
    {
        // 2026-03-08 is the 2nd Sunday of March. DST starts at 02:00 ET = 07:00 UTC.
        var oneMinuteBefore = new DateTimeOffset(2026, 3, 8, 6, 59, 0, TimeSpan.Zero);
        var atTransition = new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero);
        var oneMinuteAfter = new DateTimeOffset(2026, 3, 8, 7, 1, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(2), BrokerOffset.IcMarkets.At(oneMinuteBefore));
        Assert.Equal(TimeSpan.FromHours(3), BrokerOffset.IcMarkets.At(atTransition));
        Assert.Equal(TimeSpan.FromHours(3), BrokerOffset.IcMarkets.At(oneMinuteAfter));
    }

    [Fact]
    public void IcMarkets_AcrossFallDstTransition_2026()
    {
        // 2026-11-01 is the 1st Sunday of November. DST ends at 02:00 ET = 06:00 UTC.
        var oneMinuteBefore = new DateTimeOffset(2026, 11, 1, 5, 59, 0, TimeSpan.Zero);
        var atTransition = new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);
        var oneMinuteAfter = new DateTimeOffset(2026, 11, 1, 6, 1, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(3), BrokerOffset.IcMarkets.At(oneMinuteBefore));
        Assert.Equal(TimeSpan.FromHours(2), BrokerOffset.IcMarkets.At(atTransition));
        Assert.Equal(TimeSpan.FromHours(2), BrokerOffset.IcMarkets.At(oneMinuteAfter));
    }

    [Fact]
    public void IcMarkets_HasExpectedName()
    {
        Assert.Equal("ic-markets", BrokerOffset.IcMarkets.Name);
    }

    [Fact]
    public void TryResolve_KnownName_CaseInsensitive()
    {
        Assert.Same(BrokerOffset.IcMarkets, BrokerOffset.TryResolve("ic-markets"));
        Assert.Same(BrokerOffset.IcMarkets, BrokerOffset.TryResolve("IC-MARKETS"));
    }

    [Fact]
    public void TryResolve_UnknownName_ReturnsNull()
    {
        Assert.Null(BrokerOffset.TryResolve("darwinex"));
    }

    [Fact]
    public void KnownNames_ListsIcMarkets()
    {
        Assert.Contains("ic-markets", BrokerOffset.KnownNames);
    }

    [Fact]
    public void IcMarkets_AcrossSpringDstTransition_2025_AnchoredYear()
    {
        // 2025-03-09 is the 2nd Sunday of March — proves the rule isn't 2026-specific.
        var oneMinuteBefore = new DateTimeOffset(2025, 3, 9, 6, 59, 0, TimeSpan.Zero);
        var oneMinuteAfter = new DateTimeOffset(2025, 3, 9, 7, 1, 0, TimeSpan.Zero);

        Assert.Equal(TimeSpan.FromHours(2), BrokerOffset.IcMarkets.At(oneMinuteBefore));
        Assert.Equal(TimeSpan.FromHours(3), BrokerOffset.IcMarkets.At(oneMinuteAfter));
    }
}
