using HistoricalData.Export;
using HistoricalData.Models;

namespace HistoricalData.Tests;

public sealed class TickCsvWriterTests : IDisposable
{
    private readonly string _tempDir;

    public TickCsvWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TickCsvWriterTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup; tests shouldn't fail because of leftover temp folders
        }
    }

    [Fact]
    public void Write_FormatsRowAsExpected_ForFiveDigitFx()
    {
        var time = new DateTimeOffset(2025, 1, 2, 10, 0, 0, 142, TimeSpan.Zero);
        var tick = new Tick(time, 1.03567d, 1.03570d, 0.5f, 0.7f);

        using (var writer = new TickCsvWriter(_tempDir, "EURUSD", digits: 5, utcOffset: TimeSpan.Zero))
        {
            writer.Write(tick);
        }

        var file = Path.Combine(_tempDir, "EURUSD_ticks_2025-01.csv");
        Assert.True(File.Exists(file));
        var lines = File.ReadAllLines(file);
        Assert.Single(lines);
        Assert.Equal("2025.01.02,10:00:00.142,1.03567,1.03570,0,0,6", lines[0]);
    }

    [Fact]
    public void Write_HonorsThreeDigitsForJpyAndGold()
    {
        var time = new DateTimeOffset(2025, 3, 15, 14, 30, 5, 999, TimeSpan.Zero);
        var tick = new Tick(time, 145.123d, 145.156d, 1.0f, 1.0f);

        using (var writer = new TickCsvWriter(_tempDir, "USDJPY", digits: 3, utcOffset: TimeSpan.Zero))
        {
            writer.Write(tick);
        }

        var file = Path.Combine(_tempDir, "USDJPY_ticks_2025-03.csv");
        var line = File.ReadAllLines(file).Single();
        Assert.Equal("2025.03.15,14:30:05.999,145.123,145.156,0,0,6", line);
    }

    [Fact]
    public void Write_RollsFileAtMonthBoundary()
    {
        var jan31 = new DateTimeOffset(2025, 1, 31, 23, 59, 59, 0, TimeSpan.Zero);
        var feb01 = new DateTimeOffset(2025, 2, 1, 0, 0, 0, 0, TimeSpan.Zero);

        using (var writer = new TickCsvWriter(_tempDir, "EURUSD", digits: 5, utcOffset: TimeSpan.Zero))
        {
            writer.Write(new Tick(jan31, 1.04000d, 1.04002d, 0f, 0f));
            writer.Write(new Tick(feb01, 1.04100d, 1.04102d, 0f, 0f));
            Assert.Equal(2, writer.FilesOpened);
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, "EURUSD_ticks_2025-01.csv")));
        Assert.True(File.Exists(Path.Combine(_tempDir, "EURUSD_ticks_2025-02.csv")));
    }

    [Fact]
    public void Write_AppliesUtcOffsetWhenChoosingMonthFile()
    {
        // Tick is UTC 2025-01-31 23:30 — but with +03:00 offset, local time is 2025-02-01 02:30,
        // so the tick should land in the February file.
        var utcTime = new DateTimeOffset(2025, 1, 31, 23, 30, 0, 0, TimeSpan.Zero);
        var tick = new Tick(utcTime, 1.0d, 1.0001d, 0f, 0f);

        using (var writer = new TickCsvWriter(_tempDir, "EURUSD", digits: 5, utcOffset: TimeSpan.FromHours(3)))
        {
            writer.Write(tick);
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, "EURUSD_ticks_2025-02.csv")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "EURUSD_ticks_2025-01.csv")));
    }

    [Fact]
    public void Write_TracksTotalTickCount()
    {
        using var writer = new TickCsvWriter(_tempDir, "EURUSD", digits: 5, utcOffset: TimeSpan.Zero);
        var baseTime = new DateTimeOffset(2025, 6, 1, 0, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 100; i++)
        {
            writer.Write(new Tick(baseTime.AddMilliseconds(i), 1.0d, 1.0001d, 0f, 0f));
        }

        Assert.Equal(100, writer.TicksWritten);
        Assert.Equal(1, writer.FilesOpened);
    }

    [Fact]
    public void Write_UppercasesSymbolInFilename()
    {
        using (var writer = new TickCsvWriter(_tempDir, "eurusd", digits: 5, utcOffset: TimeSpan.Zero))
        {
            writer.Write(new Tick(
                new DateTimeOffset(2025, 1, 1, 0, 0, 0, 0, TimeSpan.Zero),
                1.0d, 1.0001d, 0f, 0f));
        }

        Assert.True(File.Exists(Path.Combine(_tempDir, "EURUSD_ticks_2025-01.csv")));
    }
}
