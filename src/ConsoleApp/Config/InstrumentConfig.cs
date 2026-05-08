using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoricalData.Config;

/// <summary>
/// Per-symbol metadata loaded from <c>instruments.json</c>. Holds the
/// digits map (Dukascopy price scale per symbol) plus optional discovery
/// data: <see cref="Earliest"/> (first UTC hour available on the source,
/// populated by <c>cache discover</c>) and <see cref="Latest"/> (most
/// recent UTC hour present in the local cache, populated by
/// <c>cache update</c> after a successful run).
///
/// Existing files that only contain <c>"digits"</c> still load cleanly —
/// the new sections are optional, and <see cref="Save"/> preserves
/// whatever was there before.
/// </summary>
public sealed class InstrumentConfig
{
    public Dictionary<string, int> Digits { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EURUSD"] = 5,
        ["GBPUSD"] = 5,
        ["USDJPY"] = 3,
        ["AUDUSD"] = 5,
        ["USDCAD"] = 5,
        ["USDCHF"] = 5,
        ["NZDUSD"] = 5,
        ["EURJPY"] = 3,
        ["XAUUSD"] = 3
    };

    /// <summary>
    /// First UTC hour for which Dukascopy has tick data, per symbol.
    /// Populated by <c>cache discover</c>. A value of <c>null</c> means
    /// the symbol was probed but no data was found on the source
    /// (delisted, never listed, or wrong ticker).
    /// </summary>
    [JsonPropertyName("earliest")]
    public Dictionary<string, DateTimeOffset?> Earliest { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Most recent UTC hour for which the local cache contains tick data,
    /// per symbol. Populated by <c>cache update</c> / <c>cache catchup</c>
    /// after a successful run. Lets <c>cache catchup</c> skip hours it
    /// already has and tells the user how fresh the cache is per symbol.
    /// </summary>
    [JsonPropertyName("latest")]
    public Dictionary<string, DateTimeOffset?> Latest { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public int GetDigits(string instrument)
    {
        return Digits.TryGetValue(instrument, out var digits) ? digits : 5;
    }

    public bool TryGetDigits(string instrument, out int digits)
    {
        return Digits.TryGetValue(instrument, out digits);
    }

    public DateTimeOffset? GetEarliest(string instrument)
    {
        return Earliest.TryGetValue(instrument, out var v) ? v : null;
    }

    public DateTimeOffset? GetLatest(string instrument)
    {
        return Latest.TryGetValue(instrument, out var v) ? v : null;
    }

    public static InstrumentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new InstrumentConfig();
        }

        var json = File.ReadAllText(path);
        var loaded = JsonSerializer.Deserialize<InstrumentConfig>(json, ReadOptions)
            ?? new InstrumentConfig();

        // Defensive: deserializer may leave nulls if a section is absent.
        loaded.Digits ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        loaded.Earliest ??= new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        loaded.Latest ??= new Dictionary<string, DateTimeOffset?>(StringComparer.OrdinalIgnoreCase);
        return loaded;
    }

    /// <summary>
    /// Atomically write the config back to disk. Writes to <c>path.tmp</c>
    /// then renames over the target so a crash mid-write can't leave a
    /// half-formed file. Creates the parent directory if missing.
    /// </summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(this, WriteOptions);
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Note: deliberately NOT setting JsonIgnoreCondition.WhenWritingNull —
    // a `null` entry inside `Earliest` (e.g. "XAUUSD": null) is the explicit
    // marker for "we probed and Dukascopy doesn't have it." Suppressing those
    // would cause `cache discover` to keep re-probing unavailable symbols
    // because they look "not yet discovered" on every reload.
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}
