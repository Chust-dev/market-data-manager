using HistoricalData.Config;
using HistoricalData.Manage;

namespace HistoricalData.Tests;

/// <summary>
/// Unit tests for the pure add/remove policy used by
/// <c>cache add-symbol</c> and <c>cache remove-symbol</c>
/// (BACKLOG #22). Exhaustive against the policy table; the command
/// classes are thin glue around these helpers + file IO.
/// </summary>
public sealed class InstrumentConfigEditorTests
{
    private static InstrumentConfig EmptyConfig() => new()
    {
        Digits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    };

    // ---------- TryAdd ----------

    [Fact]
    public void Add_NewSymbol_Succeeds()
    {
        var config = EmptyConfig();
        var result = InstrumentConfigEditor.TryAdd(config, "EURGBP", digits: 5);

        Assert.Equal(AddSymbolResult.Ok, result);
        Assert.True(config.Digits.ContainsKey("EURGBP"));
        Assert.Equal(5, config.Digits["EURGBP"]);
    }

    [Fact]
    public void Add_NormalizesSymbolToUppercase()
    {
        var config = EmptyConfig();
        InstrumentConfigEditor.TryAdd(config, "eurgbp", digits: 5);
        // The added entry is stored as uppercase. The dict itself happens
        // to be case-insensitive (so both lookups succeed), but the stored
        // KEY is "EURGBP" -- verify by walking the keys directly.
        Assert.Contains("EURGBP", config.Digits.Keys);
        Assert.DoesNotContain("eurgbp", (IEnumerable<string>)config.Digits.Keys);
    }

    [Fact]
    public void Add_TrimsSymbolWhitespace()
    {
        var config = EmptyConfig();
        InstrumentConfigEditor.TryAdd(config, "  EURGBP  ", digits: 5);
        Assert.True(config.Digits.ContainsKey("EURGBP"));
        Assert.False(config.Digits.ContainsKey("  EURGBP  "));
    }

    [Fact]
    public void Add_DuplicateSymbol_WithoutForce_Refuses()
    {
        var config = EmptyConfig();
        config.Digits["EURUSD"] = 5;

        var result = InstrumentConfigEditor.TryAdd(config, "EURUSD", digits: 4);

        Assert.Equal(AddSymbolResult.AlreadyExists, result);
        Assert.Equal(5, config.Digits["EURUSD"]); // not overwritten
    }

    [Fact]
    public void Add_DuplicateSymbol_WithForce_Overwrites()
    {
        var config = EmptyConfig();
        config.Digits["EURUSD"] = 5;

        var result = InstrumentConfigEditor.TryAdd(config, "EURUSD", digits: 4, force: true);

        Assert.Equal(AddSymbolResult.Ok, result);
        Assert.Equal(4, config.Digits["EURUSD"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Add_InvalidSymbol_Refuses(string? symbol)
    {
        var config = EmptyConfig();
        var result = InstrumentConfigEditor.TryAdd(config, symbol!, digits: 5);
        Assert.Equal(AddSymbolResult.InvalidSymbol, result);
        Assert.Empty(config.Digits);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10)]  // exceeds MaxDigits (9)
    [InlineData(99)]
    public void Add_InvalidDigits_Refuses(int digits)
    {
        var config = EmptyConfig();
        var result = InstrumentConfigEditor.TryAdd(config, "EURUSD", digits);
        Assert.Equal(AddSymbolResult.InvalidDigits, result);
        Assert.Empty(config.Digits);
    }

    [Fact]
    public void Add_ValidEdgeCaseDigits_Accepted()
    {
        var config = EmptyConfig();
        Assert.Equal(AddSymbolResult.Ok, InstrumentConfigEditor.TryAdd(config, "MIN", digits: 1));
        Assert.Equal(AddSymbolResult.Ok, InstrumentConfigEditor.TryAdd(config, "MAX", digits: InstrumentConfigEditor.MaxDigits));
    }

    // ---------- Remove ----------

    [Fact]
    public void Remove_FromAllThreeSections_WhenAllPresent()
    {
        var config = EmptyConfig();
        config.Digits["BTCUSD"] = 2;
        config.Earliest["BTCUSD"] = new DateTimeOffset(2017, 1, 1, 0, 0, 0, TimeSpan.Zero);
        config.Latest["BTCUSD"] = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var result = InstrumentConfigEditor.Remove(config, "BTCUSD");

        Assert.True(result.RemovedFromDigits);
        Assert.True(result.RemovedFromEarliest);
        Assert.True(result.RemovedFromLatest);
        Assert.True(result.AnyRemoved);

        Assert.False(config.Digits.ContainsKey("BTCUSD"));
        Assert.False(config.Earliest.ContainsKey("BTCUSD"));
        Assert.False(config.Latest.ContainsKey("BTCUSD"));
    }

    [Fact]
    public void Remove_PartialPresence_RemovesWhatExists()
    {
        var config = EmptyConfig();
        config.Digits["EURUSD"] = 5;
        // No earliest, no latest entry.

        var result = InstrumentConfigEditor.Remove(config, "EURUSD");

        Assert.True(result.RemovedFromDigits);
        Assert.False(result.RemovedFromEarliest);
        Assert.False(result.RemovedFromLatest);
        Assert.True(result.AnyRemoved);
        Assert.Empty(config.Digits);
    }

    [Fact]
    public void Remove_NotPresentAnywhere_ReturnsAllFalse()
    {
        var config = EmptyConfig();
        config.Digits["EURUSD"] = 5;

        var result = InstrumentConfigEditor.Remove(config, "GBPUSD");

        Assert.False(result.AnyRemoved);
        // The unrelated entry is untouched.
        Assert.True(config.Digits.ContainsKey("EURUSD"));
    }

    [Fact]
    public void Remove_IsCaseInsensitive()
    {
        var config = EmptyConfig();
        config.Digits["EURUSD"] = 5;

        var result = InstrumentConfigEditor.Remove(config, "eurusd");

        Assert.True(result.RemovedFromDigits);
    }

    [Fact]
    public void Remove_TrimsSymbolWhitespace()
    {
        var config = EmptyConfig();
        config.Digits["EURUSD"] = 5;

        var result = InstrumentConfigEditor.Remove(config, "  EURUSD  ");

        Assert.True(result.RemovedFromDigits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Remove_InvalidSymbol_ReturnsAllFalse(string? symbol)
    {
        var config = EmptyConfig();
        config.Digits["EURUSD"] = 5;

        var result = InstrumentConfigEditor.Remove(config, symbol!);

        Assert.False(result.AnyRemoved);
        Assert.True(config.Digits.ContainsKey("EURUSD"));
    }

    [Fact]
    public void Remove_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InstrumentConfigEditor.Remove(null!, "EURUSD"));
    }

    [Fact]
    public void Add_NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            InstrumentConfigEditor.TryAdd(null!, "EURUSD", digits: 5));
    }
}
