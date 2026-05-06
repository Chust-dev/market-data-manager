using HistoricalData;

namespace HistoricalData.Tests;

public sealed class OptionParsingTests
{
    [Fact]
    public void FromArgs_ParsesSymbolsList()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["symbols"] = "EURUSD,XAUUSD"
        };

        var options = AppOptions.FromArgs(args);

        Assert.Equal("EURUSD,XAUUSD", options.Instruments);
    }

    [Fact]
    public void FromArgs_KeepsInstrumentsConfigPathSeparate()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["instruments"] = "./custom-instruments.json",
            ["symbols"] = "all"
        };

        var options = AppOptions.FromArgs(args);

        Assert.Equal("./custom-instruments.json", options.InstrumentsPath);
        Assert.Equal("all", options.Instruments);
    }

    [Fact]
    public void FromArgs_ParsesDigitsOverrides()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["digits"] = "3",
            ["digits-map"] = "XAUUSD=3,USDJPY=3"
        };

        var options = AppOptions.FromArgs(args);

        Assert.Equal(3, options.Digits);
        Assert.Equal("XAUUSD=3,USDJPY=3", options.DigitsMap);
    }

    [Fact]
    public void FromArgs_ExportTicks_DefaultsToFalse()
    {
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var options = AppOptions.FromArgs(args);
        Assert.False(options.ExportTicks);
    }

    [Fact]
    public void FromArgs_ExportTicks_ParsesPresenceFlag()
    {
        // ArgParser turns "--export-ticks" with no following value into ["export-ticks"] = "true"
        var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["export-ticks"] = "true"
        };

        var options = AppOptions.FromArgs(args);
        Assert.True(options.ExportTicks);
    }
}
