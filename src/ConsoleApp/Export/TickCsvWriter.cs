using System.Globalization;
using System.Text;
using HistoricalData.Models;

namespace HistoricalData.Export;

/// <summary>
/// Streaming writer that exports Dukascopy ticks to MT5-compatible CSV files,
/// partitioned by calendar month so individual files stay manageable.
///
/// File naming: {symbol}_ticks_{yyyy-MM}.csv (e.g. EURUSD_ticks_2025-01.csv)
///
/// Row format (comma-separated, MT5 Symbol Editor "Import Ticks" compatible):
///     yyyy.MM.dd,HH:mm:ss.fff,bid,ask,last,volume,flags
///
/// last and volume are written as 0 (Dukascopy doesn't publish them for FX).
/// flags = 6 (TICK_FLAG_BID | TICK_FLAG_ASK) since every Dukascopy tick has bid and ask.
///
/// Ticks are expected to arrive in chronological order; the writer simply rolls
/// the file when the month changes. Call Dispose() (or use a `using` block) to
/// flush and close the final file.
/// </summary>
public sealed class TickCsvWriter : IDisposable
{
    // MT5 ENUM_TICK_FLAG: BID = 0x02, ASK = 0x04 -> BID|ASK = 0x06
    private const int FlagBidAsk = 6;
    private const int BufferSize = 1024 * 64;

    private readonly string _outputDir;
    private readonly string _symbol;
    private readonly TimeSpan _utcOffset;
    private readonly string _priceFormat;

    private StreamWriter? _writer;
    private string? _currentMonthKey;
    private long _ticksWritten;
    private int _filesOpened;

    public TickCsvWriter(string outputDir, string symbol, int digits, TimeSpan utcOffset)
    {
        if (string.IsNullOrWhiteSpace(outputDir)) throw new ArgumentException("outputDir is required", nameof(outputDir));
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol is required", nameof(symbol));
        if (digits < 0 || digits > 8) throw new ArgumentOutOfRangeException(nameof(digits));

        _outputDir = outputDir;
        _symbol = symbol.ToUpperInvariant();
        _utcOffset = utcOffset;
        _priceFormat = "F" + digits;
        Directory.CreateDirectory(outputDir);
    }

    public long TicksWritten => _ticksWritten;
    public int FilesOpened => _filesOpened;
    public string? CurrentFile { get; private set; }

    public void Write(Tick tick)
    {
        var local = tick.Time + _utcOffset;
        var monthKey = local.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        if (monthKey != _currentMonthKey)
        {
            RollFile(monthKey);
        }

        var date = local.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        var time = local.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var bid = tick.Bid.ToString(_priceFormat, CultureInfo.InvariantCulture);
        var ask = tick.Ask.ToString(_priceFormat, CultureInfo.InvariantCulture);

        // Hand-built line for speed; this is the hot path on multi-billion-tick exports.
        _writer!.Write(date);
        _writer.Write(',');
        _writer.Write(time);
        _writer.Write(',');
        _writer.Write(bid);
        _writer.Write(',');
        _writer.Write(ask);
        _writer.Write(",0,0,");
        _writer.Write(FlagBidAsk);
        _writer.Write('\n');

        _ticksWritten++;
    }

    private void RollFile(string monthKey)
    {
        _writer?.Flush();
        _writer?.Dispose();

        var path = Path.Combine(_outputDir, $"{_symbol}_ticks_{monthKey}.csv");
        _writer = new StreamWriter(path, append: false, encoding: Encoding.UTF8, bufferSize: BufferSize);
        _currentMonthKey = monthKey;
        CurrentFile = path;
        _filesOpened++;
    }

    public void Dispose()
    {
        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }
}
