using System.Globalization;
using System.Text;
using HistoricalData.Models;

namespace HistoricalData.Export;

public static class CsvWriter
{
    public static void Write(string path, IEnumerable<Bar> bars)
    {
        using var writer = new StreamWriter(path, append: false, encoding: Encoding.UTF8, bufferSize: 1024 * 64);
        foreach (var bar in bars)
        {
            var date = bar.Time.DateTime.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
            var time = bar.Time.DateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
            writer.WriteLine(string.Join(",", new[]
            {
                date,
                time,
                bar.Open.ToString(CultureInfo.InvariantCulture),
                bar.High.ToString(CultureInfo.InvariantCulture),
                bar.Low.ToString(CultureInfo.InvariantCulture),
                bar.Close.ToString(CultureInfo.InvariantCulture),
                bar.Volume.ToString(CultureInfo.InvariantCulture),
                bar.Spread.ToString(CultureInfo.InvariantCulture),
                bar.RealVolume.ToString(CultureInfo.InvariantCulture)
            }));
        }
    }
}
