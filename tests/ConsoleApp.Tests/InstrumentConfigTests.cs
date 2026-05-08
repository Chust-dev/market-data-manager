using HistoricalData.Config;

namespace HistoricalData.Tests;

/// <summary>
/// Round-trip tests for instruments.json (de)serialization. The discovery
/// pillar (`cache discover`) merges <c>earliest</c> entries into the same
/// file that holds <c>digits</c>, so round-trip and merge-preserve behaviour
/// have to be airtight — losing the digits map would silently break every
/// other command that depends on it.
/// </summary>
public sealed class InstrumentConfigTests : IDisposable
{
    private readonly string _root;

    public InstrumentConfigTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "InstrumentConfigTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public void Load_LegacyDigitsOnlyFile_ParsesAndLeavesEarliestEmpty()
    {
        var path = Path.Combine(_root, "instruments.json");
        File.WriteAllText(path, """
        {
          "digits": {
            "EURUSD": 5,
            "USDJPY": 3
          }
        }
        """);

        var config = InstrumentConfig.Load(path);

        Assert.Equal(5, config.GetDigits("EURUSD"));
        Assert.Equal(3, config.GetDigits("USDJPY"));
        Assert.Empty(config.Earliest);
        Assert.Empty(config.Latest);
    }

    [Fact]
    public void SaveThenLoad_PreservesDigitsAndEarliestAndLatest()
    {
        var path = Path.Combine(_root, "instruments.json");
        var config = new InstrumentConfig();
        config.Digits["EURUSD"] = 5;
        config.Digits["XAUUSD"] = 3;
        config.Earliest["EURUSD"] = new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero);
        config.Earliest["XAUUSD"] = null;
        config.Latest["EURUSD"] = new DateTimeOffset(2026, 5, 7, 23, 0, 0, TimeSpan.Zero);

        config.Save(path);
        var roundTripped = InstrumentConfig.Load(path);

        Assert.Equal(5, roundTripped.GetDigits("EURUSD"));
        Assert.Equal(3, roundTripped.GetDigits("XAUUSD"));
        Assert.Equal(new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero), roundTripped.GetEarliest("EURUSD"));
        Assert.Null(roundTripped.GetEarliest("XAUUSD"));
        Assert.True(roundTripped.Earliest.ContainsKey("XAUUSD"));
        Assert.Equal(new DateTimeOffset(2026, 5, 7, 23, 0, 0, TimeSpan.Zero), roundTripped.GetLatest("EURUSD"));
    }

    [Fact]
    public void Save_WritesAtomically_NoTmpFileLeftBehind()
    {
        var path = Path.Combine(_root, "instruments.json");
        var config = new InstrumentConfig();
        config.Digits["EURUSD"] = 5;

        config.Save(path);

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Save_PreservesExistingDigitsWhenUpdatingEarliest()
    {
        var path = Path.Combine(_root, "instruments.json");

        // Initial write — digits only, simulating the existing instruments.json.
        var initial = new InstrumentConfig();
        initial.Digits["EURUSD"] = 5;
        initial.Digits["USDJPY"] = 3;
        initial.Save(path);

        // Discover-style update: load, add an earliest entry, save.
        var updated = InstrumentConfig.Load(path);
        updated.Earliest["EURUSD"] = new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero);
        updated.Save(path);

        // Reload — both digits entries must still be there.
        var final = InstrumentConfig.Load(path);
        Assert.Equal(5, final.GetDigits("EURUSD"));
        Assert.Equal(3, final.GetDigits("USDJPY"));
        Assert.Equal(new DateTimeOffset(2003, 5, 4, 0, 0, 0, TimeSpan.Zero), final.GetEarliest("EURUSD"));
    }

    [Fact]
    public void Load_NonExistentFile_ReturnsEmptyConfig()
    {
        var path = Path.Combine(_root, "missing.json");

        var config = InstrumentConfig.Load(path);

        Assert.NotNull(config);
        Assert.Empty(config.Earliest);
        Assert.Empty(config.Latest);
        // Defaults are seeded at construction, so digits is not empty —
        // but it shouldn't throw, and that's the contract this test cares about.
    }
}
