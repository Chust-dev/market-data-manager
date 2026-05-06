using System.Text;
using HistoricalData.Models;

namespace HistoricalData.Export;

public static class HstWriter
{
    private const int HstVersion = 501;

    public static void Write(string path, IEnumerable<Bar> bars, string symbol, int digits, int timeframeMinutes)
    {
        using var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, FileOptions.SequentialScan);
        using var bufferedStream = new BufferedStream(fileStream, 1024 * 64);
        using var writer = new BinaryWriter(bufferedStream, Encoding.ASCII, leaveOpen: false);

        WriteHeader(writer, symbol, digits, timeframeMinutes);
        foreach (var bar in bars)
        {
            writer.Write(bar.Time.ToUnixTimeSeconds());
            writer.Write(bar.Open);
            writer.Write(bar.High);
            writer.Write(bar.Low);
            writer.Write(bar.Close);
            writer.Write(bar.Volume);
            writer.Write(bar.Spread);
            writer.Write(bar.RealVolume);
        }
    }

    private static void WriteHeader(BinaryWriter writer, string symbol, int digits, int timeframeMinutes)
    {
        writer.Write(HstVersion);
        WriteFixedString(writer, "Dukascopy to MT5", 64);
        WriteFixedString(writer, symbol, 12);
        writer.Write(timeframeMinutes);
        writer.Write(digits);
        writer.Write((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        writer.Write((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        for (var i = 0; i < 13; i++)
        {
            writer.Write(0);
        }
    }

    private static void WriteFixedString(BinaryWriter writer, string value, int length)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length >= length)
        {
            writer.Write(bytes, 0, length);
            return;
        }

        writer.Write(bytes);
        writer.Write(new byte[length - bytes.Length]);
    }
}
