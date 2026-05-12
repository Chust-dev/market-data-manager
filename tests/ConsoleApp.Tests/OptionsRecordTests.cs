using HistoricalData;
using HistoricalData.Commands.Options;
using HistoricalData.Export;

namespace HistoricalData.Tests;

/// <summary>
/// Layer 2 parsing tests: each per-command Options record's FromArgs
/// builds the right shape from a raw argMap. These complement the
/// existing OptionParsingTests (which target the legacy AppOptions.FromArgs
/// path) so both CLI surfaces stay covered as the refactor progresses.
/// </summary>
public sealed class OptionsRecordTests
{
    // -- CacheAuditOptions ---------------------------------------------------

    [Fact]
    public void CacheAuditOptions_NoArgs_NoFilter_DefaultPool()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = CacheAuditOptions.FromArgs(args);

        Assert.Null(options.InstrumentFilter);
        Assert.Equal(AppOptions.Defaults.DataPoolPath, options.PoolPath);
    }

    [Fact]
    public void CacheAuditOptions_SymbolsList_BecomesFilter()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["symbols"] = "EURUSD,GBPUSD,xauusd"
        };
        var options = CacheAuditOptions.FromArgs(args);

        Assert.NotNull(options.InstrumentFilter);
        Assert.Equal(new[] { "EURUSD", "GBPUSD", "XAUUSD" }, options.InstrumentFilter);
    }

    [Fact]
    public void CacheAuditOptions_SymbolsAll_NoFilter()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["symbols"] = "all"
        };
        Assert.Null(CacheAuditOptions.FromArgs(args).InstrumentFilter);
    }

    [Fact]
    public void CacheAuditOptions_ExplicitInstrument_BecomesFilter()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["instrument"] = "eurusd"
        };
        var options = CacheAuditOptions.FromArgs(args);

        Assert.NotNull(options.InstrumentFilter);
        Assert.Single(options.InstrumentFilter, "EURUSD");
    }

    [Fact]
    public void CacheAuditOptions_CustomPoolPath()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pool"] = @"E:\custom\pool"
        };
        Assert.Equal(@"E:\custom\pool", CacheAuditOptions.FromArgs(args).PoolPath);
    }

    // -- CacheUpdateOptions --------------------------------------------------

    [Fact]
    public void CacheUpdateOptions_NoArgs_UsesDefaults()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = CacheUpdateOptions.FromArgs(args);

        Assert.Equal(AppOptions.Defaults.Instrument, options.Instrument);
        Assert.Equal(AppOptions.Defaults.DownloadMode, options.DownloadMode);
        Assert.Equal(AppOptions.Defaults.RecentRefreshDays, options.RecentRefreshDays);
        Assert.True(options.RefreshCache);
        Assert.True(options.VerifyChecksum);
    }

    [Fact]
    public void CacheUpdateOptions_NoRefresh_TogglesRefreshCache()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["no-refresh"] = "true"
        };
        Assert.False(CacheUpdateOptions.FromArgs(args).RefreshCache);
    }

    [Fact]
    public void CacheUpdateOptions_DownloadModeDirect_Parses()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["mode"] = "direct"
        };
        Assert.Equal(DownloadMode.DirectM1, CacheUpdateOptions.FromArgs(args).DownloadMode);
    }

    [Fact]
    public void CacheUpdateOptions_StartEndOverride()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["start"] = "2024-06-01T00:00:00Z",
            ["end"] = "2024-07-01T00:00:00Z"
        };
        var options = CacheUpdateOptions.FromArgs(args);

        Assert.Equal(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), options.Start);
        Assert.Equal(new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero), options.End);
    }

    // -- BarExportOptions ----------------------------------------------------

    [Fact]
    public void BarExportOptions_NoArgs_UsesDefaultsAndCsvHst()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = BarExportOptions.FromArgs(args);

        Assert.Equal(AppOptions.Defaults.Timeframe, options.Timeframe);
        Assert.Equal(OutputFormat.CsvHst, options.OutputFormat);
    }

    [Fact]
    public void BarExportOptions_FormatCsv_ParsesCsvOnly()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["format"] = "csv"
        };
        Assert.Equal(OutputFormat.CsvOnly, BarExportOptions.FromArgs(args).OutputFormat);
    }

    [Fact]
    public void BarExportOptions_TimeframeOverride()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["timeframe"] = "h1"
        };
        Assert.Equal("h1", BarExportOptions.FromArgs(args).Timeframe);
    }

    [Fact]
    public void BarExportOptions_OffsetFlag_ProducesFixedOffset()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["offset"] = "+02:00"
        };
        var offset = BarExportOptions.FromArgs(args).Offset;
        Assert.Equal(TimeSpan.FromHours(2), offset.At(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void BarExportOptions_BrokerIcMarkets_ResolvesProfile()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["broker"] = "ic-markets"
        };
        Assert.Equal("ic-markets", BarExportOptions.FromArgs(args).Offset.Name);
    }

    [Fact]
    public void BarExportOptions_UnknownBroker_Throws()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["broker"] = "bogus"
        };
        var ex = Assert.Throws<ArgumentException>(() => BarExportOptions.FromArgs(args));
        Assert.Contains("ic-markets", ex.Message);
    }

    [Fact]
    public void BarExportOptions_BrokerAndOffsetTogether_Throws()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["broker"] = "ic-markets",
            ["offset"] = "+02:00"
        };
        Assert.Throws<ArgumentException>(() => BarExportOptions.FromArgs(args));
    }

    // -- TickExportOptions ---------------------------------------------------

    [Fact]
    public void TickExportOptions_NoArgs_UsesDefaults()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = TickExportOptions.FromArgs(args);

        Assert.Equal(AppOptions.Defaults.Instrument, options.Instrument);
        Assert.Equal(TimeSpan.Zero, options.Offset.At(DateTimeOffset.UtcNow));
        Assert.Equal(AppOptions.Defaults.DataPoolPath, options.PoolPath);
        Assert.Equal(AppOptions.Defaults.OutputPath, options.OutputPath);
    }

    [Fact]
    public void TickExportOptions_OutputPathOverride()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["output"] = @"E:\custom\out"
        };
        Assert.Equal(@"E:\custom\out", TickExportOptions.FromArgs(args).OutputPath);
    }

    [Fact]
    public void TickExportOptions_NoPrompt_SetsNonInteractive()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["no-prompt"] = "true"
        };
        Assert.True(TickExportOptions.FromArgs(args).NonInteractive);
    }

    [Fact]
    public void TickExportOptions_QuietFlag_SetsVerboseFalse()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["quiet"] = "true"
        };
        Assert.False(TickExportOptions.FromArgs(args).Verbose);
    }
}
