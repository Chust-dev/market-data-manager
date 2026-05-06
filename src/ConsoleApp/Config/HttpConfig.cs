using System.Text.Json;

namespace HistoricalData.Config;

public sealed class HttpConfig
{
    public List<string> BaseUrls { get; set; } = new() { "https://datafeed.dukascopy.com/datafeed" };
    public int RetryCount { get; set; } = 3;
    public int RetryBackoffSeconds { get; set; } = 2;
    public int TimeoutSeconds { get; set; } = 30;

    public static HttpConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new HttpConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<HttpConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new HttpConfig();
    }
}
