using System.Text;
using HistoricalData.Config;
using HistoricalData.DataPool;
using HistoricalData.Export;
using HistoricalData.Models;
using HistoricalData.Utils;

namespace HistoricalData.Tests;

public sealed class CoreTests
{
    [Fact]
    public void BarAggregator_BuildsSingleMinuteBar()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator("m1", 5, TimeSpan.Zero, filterWeekends: true, start, end);

        aggregator.AddTick(new Tick(start.AddSeconds(1), 1.23456, 1.23466, 1.0f, 1.0f));
        aggregator.AddTick(new Tick(start.AddSeconds(30), 1.23450, 1.23460, 1.0f, 1.0f));
        aggregator.AddTick(new Tick(start.AddSeconds(59), 1.23480, 1.23490, 1.0f, 1.0f));

        var bars = aggregator.GetBars();
        Assert.Single(bars);
        var bar = bars[0];
        Assert.Equal(start, bar.Time);
        Assert.Equal(1.23456, bar.Open);
        Assert.Equal(1.23480, bar.High);
        Assert.Equal(1.23450, bar.Low);
        Assert.Equal(1.23480, bar.Close);
    }

    [Fact]
    public void BarAggregator_FiltersWeekendBars()
    {
        var saturday = new DateTimeOffset(2025, 1, 4, 0, 0, 0, TimeSpan.Zero);
        var end = saturday.AddMinutes(1);
        var aggregator = new BarAggregator("m1", 5, TimeSpan.Zero, filterWeekends: true, saturday, end);

        aggregator.AddTick(new Tick(saturday.AddSeconds(5), 1.11111, 1.11121, 1.0f, 1.0f));

        var bars = aggregator.GetBars();
        Assert.Empty(bars);
    }

    [Fact]
    public void BarAggregator_DeduplicatesIdenticalTicks_WhenEnabled()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator("m1", 5, TimeSpan.Zero, filterWeekends: false, start, end, deduplicateTicks: true);

        var tick = new Tick(start.AddSeconds(10), 1.23456, 1.23466, 1.0f, 1.0f);
        aggregator.AddTick(tick);
        aggregator.AddTick(tick);

        var bars = aggregator.GetBars();
        Assert.Single(bars);
        Assert.Equal(1, bars[0].Volume);
        Assert.Equal(1, aggregator.DuplicateTicksDropped);
    }

    [Fact]
    public void BarAggregator_SkipsFallbackBar_WhenMinuteHasTicks()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator("m1", 5, TimeSpan.Zero, filterWeekends: false, start, end, deduplicateTicks: true, skipFallbackIfTicked: true);

        aggregator.AddTick(new Tick(start.AddSeconds(5), 1.1, 1.2, 1.0f, 1.0f));
        aggregator.AddBar(new Bar(start, 1.0, 1.3, 0.9, 1.2, 100, 1, 1));

        var bars = aggregator.GetBars();
        Assert.Single(bars);
        Assert.Equal(1, bars[0].Volume);
        Assert.Equal(1, aggregator.FallbackBarsSkipped);
    }

    [Fact]
    public void BarAggregator_TryAddFallbackBar_OnlyIfMissing()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddMinutes(1);
        var aggregator = new BarAggregator("m1", 5, TimeSpan.Zero, filterWeekends: false, start, end, deduplicateTicks: false, skipFallbackIfTicked: true);

        var bar = new Bar(start, 1.0, 1.1, 0.9, 1.05, 10, 1, 1);
        Assert.True(aggregator.TryAddFallbackBar(bar, onlyIfMissing: true));
        Assert.False(aggregator.TryAddFallbackBar(bar, onlyIfMissing: true));

        var bars = aggregator.GetBars();
        Assert.Single(bars);
    }

    [Fact]
    public void BarResampler_AggregatesToFiveMinutes()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<Bar>
        {
            new(start, 1.1, 1.2, 1.0, 1.15, 10, 2, 3),
            new(start.AddMinutes(1), 1.15, 1.25, 1.12, 1.22, 11, 2, 3),
            new(start.AddMinutes(2), 1.22, 1.3, 1.18, 1.28, 12, 3, 4)
        };

        var resampled = BarResampler.Resample(bars, 5);
        Assert.Single(resampled);
        var bar = resampled[0];
        Assert.Equal(start, bar.Time);
        Assert.Equal(1.1, bar.Open);
        Assert.Equal(1.3, bar.High);
        Assert.Equal(1.0, bar.Low);
        Assert.Equal(1.28, bar.Close);
        Assert.Equal(33, bar.Volume);
        Assert.Equal(3, bar.Spread);
        Assert.Equal(10, bar.RealVolume);
    }

    [Fact]
    public void BarResampler_SortsWhenInputUnordered()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<Bar>
        {
            new(start.AddMinutes(2), 1.2, 1.3, 1.1, 1.25, 12, 2, 3),
            new(start, 1.0, 1.1, 0.9, 1.05, 10, 1, 2),
            new(start.AddMinutes(1), 1.05, 1.2, 1.0, 1.15, 11, 2, 3)
        };

        var resampled = BarResampler.Resample(bars, 5);
        Assert.Single(resampled);
        Assert.Equal(start, resampled[0].Time);
        Assert.Equal(1.0, resampled[0].Open);
        Assert.Equal(1.3, resampled[0].High);
        Assert.Equal(0.9, resampled[0].Low);
        Assert.Equal(1.25, resampled[0].Close);
        Assert.Equal(33, resampled[0].Volume);
    }

    [Fact]
    public void BarResampler_AggregatesToFourHoursAligned()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 10, 0, TimeSpan.Zero);
        var bars = new List<Bar>
        {
            new(start, 1.1, 1.2, 1.0, 1.15, 10, 2, 3),
            new(start.AddHours(1), 1.15, 1.25, 1.12, 1.22, 11, 2, 3),
            new(start.AddHours(3).AddMinutes(40), 1.22, 1.3, 1.18, 1.28, 12, 3, 4),
            new(start.AddHours(4), 1.28, 1.35, 1.2, 1.31, 9, 2, 2)
        };

        Assert.True(TimeframeUtils.TryParse("h4", out var timeframe));
        var resampled = BarResampler.Resample(bars, timeframe);

        Assert.Equal(2, resampled.Count);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), resampled[0].Time);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 4, 0, 0, TimeSpan.Zero), resampled[1].Time);
        Assert.Equal(33, resampled[0].Volume);
        Assert.Equal(9, resampled[1].Volume);
    }

    [Fact]
    public void BarResampler_AggregatesToDayAligned()
    {
        var bars = new List<Bar>
        {
            new(new DateTimeOffset(2025, 1, 2, 10, 0, 0, TimeSpan.Zero), 1.1, 1.2, 1.0, 1.15, 10, 2, 3),
            new(new DateTimeOffset(2025, 1, 2, 20, 0, 0, TimeSpan.Zero), 1.15, 1.25, 1.12, 1.22, 11, 2, 3)
        };

        Assert.True(TimeframeUtils.TryParse("d1", out var timeframe));
        var resampled = BarResampler.Resample(bars, timeframe);

        Assert.Single(resampled);
        Assert.Equal(new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero), resampled[0].Time);
    }

    [Fact]
    public void BarResampler_AggregatesToWeekAligned()
    {
        var bars = new List<Bar>
        {
            new(new DateTimeOffset(2025, 1, 8, 12, 0, 0, TimeSpan.Zero), 1.1, 1.2, 1.0, 1.15, 10, 2, 3),
            new(new DateTimeOffset(2025, 1, 9, 9, 0, 0, TimeSpan.Zero), 1.15, 1.25, 1.12, 1.22, 11, 2, 3)
        };

        Assert.True(TimeframeUtils.TryParse("w1", out var timeframe));
        var resampled = BarResampler.Resample(bars, timeframe);

        Assert.Single(resampled);
        Assert.Equal(new DateTimeOffset(2025, 1, 6, 0, 0, 0, TimeSpan.Zero), resampled[0].Time);
    }

    [Fact]
    public void BarResampler_AggregatesToMonthAligned()
    {
        var bars = new List<Bar>
        {
            new(new DateTimeOffset(2025, 3, 15, 12, 0, 0, TimeSpan.Zero), 1.1, 1.2, 1.0, 1.15, 10, 2, 3),
            new(new DateTimeOffset(2025, 3, 20, 9, 0, 0, TimeSpan.Zero), 1.15, 1.25, 1.12, 1.22, 11, 2, 3)
        };

        Assert.True(TimeframeUtils.TryParse("mn1", out var timeframe));
        var resampled = BarResampler.Resample(bars, timeframe);

        Assert.Single(resampled);
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), resampled[0].Time);
    }

    [Fact]
    public void CsvWriter_WritesExpectedFormat()
    {
        var path = Path.GetTempFileName();
        try
        {
            var time = new DateTimeOffset(2025, 1, 1, 12, 34, 0, TimeSpan.Zero);
            var bars = new[]
            {
                new Bar(time, 1.1, 1.2, 1.0, 1.15, 10, 2, 3)
            };

            CsvWriter.Write(path, bars);
            var lines = File.ReadAllLines(path);
            Assert.Single(lines);
            Assert.Equal("2025.01.01,12:34,1.1,1.2,1,1.15,10,2,3", lines[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HstWriter_WritesHeaderFields()
    {
        var path = Path.GetTempFileName();
        try
        {
            var time = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var bars = new[]
            {
                new Bar(time, 1.1, 1.2, 1.0, 1.15, 10, 2, 3)
            };

            HstWriter.Write(path, bars, "EURUSD", 5, 5);

            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            Assert.Equal(501, reader.ReadInt32());
            var copyright = Encoding.ASCII.GetString(reader.ReadBytes(64)).TrimEnd('\0');
            var symbol = Encoding.ASCII.GetString(reader.ReadBytes(12)).TrimEnd('\0');
            var timeframe = reader.ReadInt32();
            var digits = reader.ReadInt32();

            Assert.Equal("Dukascopy to MT5", copyright);
            Assert.Equal("EURUSD", symbol);
            Assert.Equal(5, timeframe);
            Assert.Equal(5, digits);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DukascopyParser_ParsesTickAndBarRecords()
    {
        var hour = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var tickData = new byte[20];
        WriteInt32BE(tickData, 0, 1000);
        WriteInt32BE(tickData, 4, 149000);
        WriteInt32BE(tickData, 8, 150000);
        WriteSingleBE(tickData, 12, 2.5f);
        WriteSingleBE(tickData, 16, 1.5f);

        var ticks = DukascopyClient.ParseTicks(tickData, hour, 5);
        Assert.Single(ticks);
        Assert.Equal(hour.AddSeconds(1), ticks[0].Time);
        Assert.Equal(1.49, ticks[0].Bid, 5);
        Assert.Equal(1.5, ticks[0].Ask, 5);

        var barData = new byte[24];
        WriteInt32BE(barData, 0, 60);
        WriteInt32BE(barData, 4, 150000);
        WriteInt32BE(barData, 8, 151000);
        WriteInt32BE(barData, 12, 149000);
        WriteInt32BE(barData, 16, 150500);
        WriteSingleBE(barData, 20, 12f);

        var bars = DukascopyClient.ParseBars(barData, hour, 5);
        Assert.Single(bars);
        Assert.Equal(hour.AddMinutes(1), bars[0].Time);
        Assert.Equal(1.5, bars[0].Open, 5);
        Assert.Equal(1.51, bars[0].High, 5);
        Assert.Equal(1.49, bars[0].Low, 5);
        Assert.Equal(1.505, bars[0].Close, 5);
        Assert.Equal(12, bars[0].Volume);
        Assert.Equal(0, bars[0].Spread);
        Assert.Equal(0, bars[0].RealVolume);
    }

    [Fact]
    public void DataPoolFileMeta_VerifiesChecksum()
    {
        var path = Path.GetTempFileName();
        var metaPath = DataPoolFileMeta.GetMetaPath(path);
        try
        {
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            DataPoolFileMeta.Write(path);
            Assert.True(DataPoolFileMeta.VerifyFile(path));

            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
            Assert.False(DataPoolFileMeta.VerifyFile(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            if (File.Exists(metaPath))
            {
                File.Delete(metaPath);
            }
        }
    }

    [Fact]
    public void SessionCalendar_RespectsHours()
    {
        var config = new SessionConfig
        {
            TimeZoneId = "UTC",
            Sessions =
            {
                new SessionConfig.SessionRule { Day = "Monday", Start = "08:00", End = "10:00" }
            }
        };

        var calendar = new SessionConfig.SessionCalendar(config);
        var mondayOpen = new DateTimeOffset(2025, 1, 6, 8, 30, 0, TimeSpan.Zero);
        var mondayClosed = new DateTimeOffset(2025, 1, 6, 10, 30, 0, TimeSpan.Zero);

        Assert.True(calendar.IsOpen(mondayOpen));
        Assert.False(calendar.IsOpen(mondayClosed));
    }

    private static void WriteInt32BE(Span<byte> buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteSingleBE(Span<byte> buffer, int offset, float value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        bytes.CopyTo(buffer[offset..(offset + 4)]);
    }
}
