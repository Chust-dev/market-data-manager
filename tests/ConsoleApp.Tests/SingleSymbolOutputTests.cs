using HistoricalData;
using HistoricalData.Commands.Options;
using HistoricalData.Export;

namespace HistoricalData.Tests;

/// <summary>
/// Marker collection for tests that mutate the process-global Console.Out.
/// All tests in this collection run serially with each other (xUnit's
/// default per-collection-sequencing), so a concurrent test in a different
/// class never writes to a stale or disposed redirected stream.
/// </summary>
[CollectionDefinition("ConsoleOut")]
public class ConsoleOutCollection { }

/// <summary>
/// BACKLOG #51: per-symbol "=== SYMBOL ===" header and "Batch summary:"
/// tally are noise when only one instrument was requested — they exist to
/// delimit and account multi-symbol output. These tests pin the gating
/// behaviour by capturing Console.Out around pillar runs.
/// </summary>
[Collection("ConsoleOut")]
public sealed class SingleSymbolOutputTests : IDisposable
{
    private readonly string _root;
    private readonly string _poolPath;
    private readonly string _outputPath;
    private readonly TextWriter _originalOut;

    public SingleSymbolOutputTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "SingleSymbolOutputTests_" + Guid.NewGuid().ToString("N"));
        _poolPath = Path.Combine(_root, "pool");
        _outputPath = Path.Combine(_root, "output");
        Directory.CreateDirectory(_poolPath);
        Directory.CreateDirectory(_outputPath);
        _originalOut = Console.Out;
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
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

    private BarExportOptions BuildOptions(string instrument, string instruments) =>
        new(
            Instrument: instrument,
            Instruments: instruments,
            Start: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero),
            End: new DateTimeOffset(2025, 1, 1, 1, 0, 0, TimeSpan.Zero),
            Timeframe: "m1",
            OutputFormat: OutputFormat.None,
            Offset: BrokerOffset.Fixed(TimeSpan.Zero),
            Spread: SpreadMethod.Last,
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
            Quiet: false);

    private async Task<string> CaptureOutput(Func<Task> body)
    {
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            await body();
        }
        finally
        {
            // Restore stdout BEFORE disposing the StringWriter so any
            // background-task console write after this point goes to the
            // real stdout, not a disposed buffer.
            Console.SetOut(_originalOut);
        }
        var text = captured.ToString();
        captured.Dispose();
        return text;
    }

    [Fact]
    public async Task BarExporter_SingleSymbol_SuppressesPerSymbolHeader()
    {
        var options = BuildOptions(instrument: "TESTSYM", instruments: "");
        var output = await CaptureOutput(async () =>
        {
            var result = await new BarExporter().RunAsync(options);
            Assert.Equal(0, result);
        });

        Assert.DoesNotContain("=== TESTSYM ===", output);
    }

    [Fact]
    public async Task BarExporter_SingleSymbol_SuppressesBatchSummary()
    {
        var options = BuildOptions(instrument: "TESTSYM", instruments: "");
        var output = await CaptureOutput(() => new BarExporter().RunAsync(options));

        Assert.DoesNotContain("Batch summary:", output);
        Assert.DoesNotContain("Requested: 1", output);
    }

    [Fact]
    public async Task BarExporter_MultipleSymbols_KeepsPerSymbolHeader()
    {
        // Two instruments via the comma-separated --symbols form.
        var options = BuildOptions(instrument: "", instruments: "FOO,BAR");
        var output = await CaptureOutput(() => new BarExporter().RunAsync(options));

        Assert.Contains("=== FOO ===", output);
        Assert.Contains("=== BAR ===", output);
    }

    [Fact]
    public async Task BarExporter_MultipleSymbols_KeepsBatchSummary()
    {
        var options = BuildOptions(instrument: "", instruments: "FOO,BAR");
        var output = await CaptureOutput(() => new BarExporter().RunAsync(options));

        Assert.Contains("Batch summary:", output);
        Assert.Contains("Requested: 2", output);
    }
}
