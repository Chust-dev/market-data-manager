using HistoricalData.Manage;

namespace HistoricalData.Tests;

/// <summary>
/// Unit tests for the transient-skip merge policy used by
/// <c>cache discover</c> (BACKLOG #54). Verifies that:
/// <list type="bullet">
///   <item>transient failures DON'T overwrite existing entries;</item>
///   <item>clean not-available 404s DO write null;</item>
///   <item>successes write the discovered earliest date.</item>
/// </list>
/// </summary>
public sealed class DiscoveryMergeTests
{
    private static DiscoveryResult Success(int year, int month, int day) =>
        new(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero), Probes: 26, Note: null, IsTransient: false);

    private static DiscoveryResult NotAvailable() =>
        new(Earliest: null, Probes: 3, Note: "not available", IsTransient: false);

    private static DiscoveryResult Transient() =>
        new(Earliest: null, Probes: 3, Note: "transient error", IsTransient: true);

    [Fact]
    public void Transient_PreservesValidExistingEntry()
    {
        // Symbol has a real previously-discovered earliest date. A new
        // transient result must not clobber it.
        var dict = new Dictionary<string, DateTimeOffset?>
        {
            ["EURUSD"] = new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero)
        };

        var written = DiscoveryMerge.TryApply(dict, "EURUSD", Transient());

        Assert.False(written);
        Assert.Single(dict);
        Assert.Equal(new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero), dict["EURUSD"]);
    }

    [Fact]
    public void Transient_PreservesNullExistingEntry()
    {
        // Symbol was previously confirmed not-available (null in the map).
        // A transient result shouldn't change that — we still know the
        // last clean signal said "not available".
        var dict = new Dictionary<string, DateTimeOffset?> { ["EXOTIC"] = null };

        var written = DiscoveryMerge.TryApply(dict, "EXOTIC", Transient());

        Assert.False(written);
        Assert.Single(dict);
        Assert.Null(dict["EXOTIC"]);
    }

    [Fact]
    public void Transient_LeavesAbsentSymbolAbsent()
    {
        // Symbol was never in the dict. Transient failure must not add it,
        // so the idempotent-skip filter on subsequent runs retries it
        // (a symbol absent from the dict is "not yet discovered").
        var dict = new Dictionary<string, DateTimeOffset?>();

        var written = DiscoveryMerge.TryApply(dict, "NEWPAIR", Transient());

        Assert.False(written);
        Assert.Empty(dict);
    }

    [Fact]
    public void CleanNotAvailable_WritesNull()
    {
        // Every sanity probe returned 404 — symbol is genuinely not on
        // Dukascopy. Persist that as null so we don't keep retrying.
        var dict = new Dictionary<string, DateTimeOffset?>();

        var written = DiscoveryMerge.TryApply(dict, "FAKESYM", NotAvailable());

        Assert.True(written);
        Assert.True(dict.ContainsKey("FAKESYM"));
        Assert.Null(dict["FAKESYM"]);
    }

    [Fact]
    public void CleanNotAvailable_OverwritesPriorValidEntry()
    {
        // Edge case: a symbol was previously discovered, but Dukascopy has
        // since delisted it. Today's clean 404s ARE a real signal — write
        // null over the stale date.
        var dict = new Dictionary<string, DateTimeOffset?>
        {
            ["DELISTED"] = new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var written = DiscoveryMerge.TryApply(dict, "DELISTED", NotAvailable());

        Assert.True(written);
        Assert.Null(dict["DELISTED"]);
    }

    [Fact]
    public void Success_WritesEarliestDate()
    {
        var dict = new Dictionary<string, DateTimeOffset?>();

        var written = DiscoveryMerge.TryApply(dict, "EURUSD", Success(2003, 5, 4));

        Assert.True(written);
        Assert.Equal(new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero), dict["EURUSD"]);
    }

    [Fact]
    public void Success_OverwritesPriorEntry()
    {
        // A refresh run finds a later earliest date (Dukascopy data
        // start shifted, e.g. after a re-import). Overwrite.
        var dict = new Dictionary<string, DateTimeOffset?>
        {
            ["EURUSD"] = new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero)
        };

        DiscoveryMerge.TryApply(dict, "EURUSD", Success(2003, 4, 5));

        Assert.Equal(new DateTimeOffset(2003, 4, 5, 0, 0, 0, TimeSpan.Zero), dict["EURUSD"]);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var dict = new Dictionary<string, DateTimeOffset?>();
        var result = Success(2003, 5, 4);

        Assert.Throws<ArgumentNullException>(() =>
            DiscoveryMerge.TryApply(null!, "EURUSD", result));
        Assert.Throws<ArgumentException>(() =>
            DiscoveryMerge.TryApply(dict, "", result));
        Assert.Throws<ArgumentNullException>(() =>
            DiscoveryMerge.TryApply(dict, "EURUSD", null!));
    }
}
