using System.Text.Json;

namespace HistoricalData.Config;

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

    public int GetDigits(string instrument)
    {
        return Digits.TryGetValue(instrument, out var digits) ? digits : 5;
    }

    public bool TryGetDigits(string instrument, out int digits)
    {
        return Digits.TryGetValue(instrument, out digits);
    }

    public static InstrumentConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new InstrumentConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<InstrumentConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new InstrumentConfig();
    }
}
