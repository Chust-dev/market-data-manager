using HistoricalData;
using HistoricalData.Commands.Options;
using HistoricalData.Export;

namespace HistoricalData.Tests;

/// <summary>
/// Smoke tests for the Layer 3 pillar classes. These confirm each pillar's
/// <c>RunAsync</c> entry point completes successfully against an empty
/// temp pool — exercising the orchestration (load configs, resolve
/// instruments, build paths, set up cancellation, loop, batch summary)
/// without needing network access or real cache files.
///
/// `Downloader` isn't covered here because its first per-instrument step
/// is a network probe to Dukascopy. That requires either real network
/// access or a mocking layer that doesn't yet exist; meaningful tests for
/// it would be a follow-up commit when we extract <c>DukascopyClient</c>
/// behind an interface.
/// </summary>
public sealed class PillarSmokeTests : IDisposable
{
    private readonly string _root;
    private readonly string _poolPath;
    private readonly string _outputPath;

    public PillarSmokeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PillarSmokeTests_" + Guid.NewGuid().ToString("N"));
        _poolPath = Path.Combine(_root, "pool");
        _outputPath = Path.Combine(_root, "output");
        Directory.CreateDirectory(_poolPath);
        Directory.CreateDirectory(_outputPath);
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
    public async Task BarExporter_EmptyPool_SucceedsAndWritesEmptyCsv()
    {
        var options = new BarExportOptions(
            Instrument: "TESTSYM",
            Instruments: "",
            Start: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            End: new DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero),
            Timeframe: "m1",
            OutputFormat: OutputFormat.CsvOnly,
            UtcOffset: TimeSpan.Zero,
            PoolPath: _poolPath,
            OutputPath: _outputPath,
            HttpConfigPath: Path.Combine(_root, "no-http.json"),
            InstrumentsConfigPath: Path.Combine(_root, "no-instruments.json"),
            DeduplicateTicks: true,
            SkipFallbackIfTicked: true,
            UseSessionCalendar: false,
            SessionConfigPath: Path.Combine(_root, "no-sessions.json"),
            Digits: 5,
            DigitsMap: "",
            NonInteractive: true,
            Verbose: false,
            Quiet: true);

        var result = await new BarExporter().RunAsync(options);

        Assert.Equal(0, result);
        // Empty cache → empty bar list → an empty CSV is still written.
        Assert.True(File.Exists(Path.Combine(_outputPath, "TESTSYM_m1.csv")));
    }

    [Fact]
    public async Task BarExporter_FormatNone_WritesNothing()
    {
        var options = new BarExportOptions(
            Instrument: "TESTSYM",
            Instruments: "",
            Start: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            End: new DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero),
            Timeframe: "m1",
            OutputFormat: OutputFormat.None,
            UtcOffset: TimeSpan.Zero,
            PoolPath: _poolPath,
            OutputPath: _outputPath,
            HttpConfigPath: Path.Combine(_root, "no-http.json"),
            InstrumentsConfigPath: Path.Combine(_root, "no-instruments.json"),
            DeduplicateTicks: true,
            SkipFallbackIfTicked: true,
            UseSessionCalendar: false,
            SessionConfigPath: Path.Combine(_root, "no-sessions.json"),
            Digits: 5,
            DigitsMap: "",
            NonInteractive: true,
            Verbose: false,
            Quiet: true);

        var result = await new BarExporter().RunAsync(options);

        Assert.Equal(0, result);
        Assert.False(File.Exists(Path.Combine(_outputPath, "TESTSYM_m1.csv")));
        Assert.False(File.Exists(Path.Combine(_outputPath, "TESTSYM_m1.hst")));
    }

    [Fact]
    public async Task TickExporter_EmptyPool_SucceedsWithoutWritingTickFiles()
    {
        var options = new TickExportOptions(
            Instrument: "TESTSYM",
            Instruments: "",
            Start: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            End: new DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero),
            UtcOffset: TimeSpan.Zero,
            PoolPath: _poolPath,
            OutputPath: _outputPath,
            HttpConfigPath: Path.Combine(_root, "no-http.json"),
            InstrumentsConfigPath: Path.Combine(_root, "no-instruments.json"),
            Digits: 5,
            DigitsMap: "",
            NonInteractive: true,
            Verbose: false,
            Quiet: true);

        var result = await new TickExporter().RunAsync(options);

        Assert.Equal(0, result);
        // Empty cache → no monthly tick files created (TickCsvWriter only opens files when ticks arrive).
        var tickFiles = Directory.GetFiles(_outputPath, "TESTSYM_ticks_*.csv");
        Assert.Empty(tickFiles);
    }

    [Fact]
    public async Task BarExporter_NoInstrumentsRequested_ReturnsErrorWithoutCrashing()
    {
        var options = new BarExportOptions(
            Instrument: "",
            Instruments: "",
            Start: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            End: new DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero),
            Timeframe: "m1",
            OutputFormat: OutputFormat.CsvOnly,
            UtcOffset: TimeSpan.Zero,
            PoolPath: _poolPath,
            OutputPath: _outputPath,
            HttpConfigPath: Path.Combine(_root, "no-http.json"),
            InstrumentsConfigPath: Path.Combine(_root, "no-instruments.json"),
            DeduplicateTicks: true,
            SkipFallbackIfTicked: true,
            UseSessionCalendar: false,
            SessionConfigPath: Path.Combine(_root, "no-sessions.json"),
            Digits: 5,
            DigitsMap: "",
            NonInteractive: true,
            Verbose: false,
            Quiet: true);

        var result = await new BarExporter().RunAsync(options);

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task TickExporter_NoInstrumentsRequested_ReturnsErrorWithoutCrashing()
    {
        var options = new TickExportOptions(
            Instrument: "",
            Instruments: "",
            Start: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            End: new DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero),
            UtcOffset: TimeSpan.Zero,
            PoolPath: _poolPath,
            OutputPath: _outputPath,
            HttpConfigPath: Path.Combine(_root, "no-http.json"),
            InstrumentsConfigPath: Path.Combine(_root, "no-instruments.json"),
            Digits: 5,
            DigitsMap: "",
            NonInteractive: true,
            Verbose: false,
            Quiet: true);

        var result = await new TickExporter().RunAsync(options);

        Assert.Equal(1, result);
    }
}
