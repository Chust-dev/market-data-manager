using System.Globalization;
using System.Text;

namespace HistoricalData.Manage;

/// <summary>
/// Fast directory-level audit of a Dukascopy data pool. Walks the
/// pool/symbol/year/month/day file structure, counts cached .bi5 files,
/// computes date coverage, and flags zero-byte (corrupt) downloads.
///
/// Does NOT decompress the .bi5 files — this stays at "seconds, not minutes"
/// even for full-history pools. A deeper tick-level audit would be a
/// separate pass.
/// </summary>
public sealed class PoolAuditor
{
    private const string TicksFileSuffix = "_ticks.bi5";
    private const string DailyM1FileName = "BID_candles_min_1.bi5";

    // Approximate share of weekday hours in a calendar week.
    // Forex market: Mon 00:00 UTC – Fri 21:00 UTC ≈ 117 of 168 hours,
    // so ~70% of all hours are "expected to have data."
    private const double WeekdayHoursFraction = 117.0 / 168.0;

    private readonly string _poolPath;

    public PoolAuditor(string poolPath)
    {
        _poolPath = poolPath ?? throw new ArgumentNullException(nameof(poolPath));
    }

    public PoolAuditReport Audit(IReadOnlyCollection<string>? symbolFilter = null)
    {
        var report = new PoolAuditReport
        {
            PoolPath = _poolPath,
            PoolExists = Directory.Exists(_poolPath)
        };

        if (!report.PoolExists)
        {
            return report;
        }

        var filter = symbolFilter is null
            ? null
            : new HashSet<string>(symbolFilter, StringComparer.OrdinalIgnoreCase);

        foreach (var symbolDir in Directory.EnumerateDirectories(_poolPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var symbol = Path.GetFileName(symbolDir);
            if (filter is not null && !filter.Contains(symbol))
            {
                continue;
            }

            report.Symbols.Add(AuditSymbol(symbol, symbolDir));
        }

        return report;
    }

    private static SymbolAuditReport AuditSymbol(string symbol, string symbolDir)
    {
        var result = new SymbolAuditReport { Symbol = symbol };

        DateTimeOffset? firstHour = null;
        DateTimeOffset? lastHour = null;

        foreach (var yearDir in Directory.EnumerateDirectories(symbolDir))
        {
            if (!int.TryParse(Path.GetFileName(yearDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                continue;
            }

            foreach (var monthDir in Directory.EnumerateDirectories(yearDir))
            {
                if (!int.TryParse(Path.GetFileName(monthDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out var dukaMonth))
                {
                    continue;
                }

                // Dukascopy's URL convention is 0-indexed months. Map to calendar month for date math.
                var calMonth = dukaMonth + 1;
                if (calMonth is < 1 or > 12)
                {
                    continue;
                }

                foreach (var dayDir in Directory.EnumerateDirectories(monthDir))
                {
                    if (!int.TryParse(Path.GetFileName(dayDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
                    {
                        continue;
                    }

                    if (!IsValidDate(year, calMonth, day))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(dayDir, "*.bi5"))
                    {
                        var info = new FileInfo(file);
                        result.TotalBytes += info.Length;

                        if (info.Length == 0)
                        {
                            result.EmptyFiles++;
                            continue;
                        }

                        var name = info.Name;
                        if (name.EndsWith(TicksFileSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            result.HourTickFiles++;
                            if (TryParseHour(name, out var hour))
                            {
                                var ts = new DateTimeOffset(year, calMonth, day, hour, 0, 0, TimeSpan.Zero);
                                if (firstHour is null || ts < firstHour) firstHour = ts;
                                if (lastHour is null || ts > lastHour) lastHour = ts;
                            }
                        }
                        else if (name.Equals(DailyM1FileName, StringComparison.OrdinalIgnoreCase))
                        {
                            result.DailyM1Files++;
                        }
                        else
                        {
                            result.UnexpectedFiles++;
                        }
                    }
                }
            }
        }

        result.FirstHour = firstHour;
        result.LastHour = lastHour;

        if (firstHour is not null && lastHour is not null && result.HourTickFiles > 0)
        {
            var totalHours = (long)Math.Floor((lastHour.Value - firstHour.Value).TotalHours) + 1;
            var expected = (long)Math.Round(totalHours * WeekdayHoursFraction);
            result.ExpectedWeekdayHours = expected;
            result.CoverageRate = expected > 0 ? Math.Min(1.0, (double)result.HourTickFiles / expected) : 0;
        }

        return result;
    }

    private static bool TryParseHour(string fileName, out int hour)
    {
        hour = 0;
        if (fileName.Length < 2) return false;
        return int.TryParse(fileName.AsSpan(0, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)
               && hour is >= 0 and <= 23;
    }

    private static bool IsValidDate(int year, int month, int day)
    {
        try
        {
            _ = new DateTime(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

public sealed class PoolAuditReport
{
    public required string PoolPath { get; init; }
    public bool PoolExists { get; init; }
    public List<SymbolAuditReport> Symbols { get; } = new();

    public long TotalBytes => Symbols.Sum(s => s.TotalBytes);
    public int TotalHourTickFiles => Symbols.Sum(s => s.HourTickFiles);
    public int TotalDailyM1Files => Symbols.Sum(s => s.DailyM1Files);
    public int TotalEmptyFiles => Symbols.Sum(s => s.EmptyFiles);
    public int TotalUnexpectedFiles => Symbols.Sum(s => s.UnexpectedFiles);

    public string Render()
    {
        var sb = new StringBuilder();
        if (!PoolExists)
        {
            sb.AppendLine($"Pool path does not exist: {PoolPath}");
            return sb.ToString();
        }

        sb.AppendLine($"Pool: {PoolPath}");
        sb.AppendLine($"Symbols cached: {Symbols.Count}");
        sb.AppendLine($"Disk usage:     {FormatBytes(TotalBytes)}");
        sb.AppendLine($"Hour tick files:{TotalHourTickFiles,12:N0}");
        sb.AppendLine($"Daily M1 files: {TotalDailyM1Files,12:N0}");
        if (TotalEmptyFiles > 0) sb.AppendLine($"Empty files:    {TotalEmptyFiles,12:N0}  (zero-byte; consider deleting)");
        if (TotalUnexpectedFiles > 0) sb.AppendLine($"Unexpected:     {TotalUnexpectedFiles,12:N0}");
        sb.AppendLine();
        sb.AppendLine("Per-symbol coverage:");
        sb.AppendLine($"  {"Symbol",-10} {"FirstHour",-20} {"LastHour",-20} {"HourFiles",10} {"DayFiles",9} {"Coverage",10} {"Disk",10} {"Empty",6}");
        foreach (var s in Symbols)
        {
            var first = s.FirstHour?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
            var last = s.LastHour?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "-";
            var coverage = s.ExpectedWeekdayHours > 0
                ? $"{s.CoverageRate:P1}"
                : "-";
            sb.AppendLine($"  {s.Symbol,-10} {first,-20} {last,-20} {s.HourTickFiles,10:N0} {s.DailyM1Files,9:N0} {coverage,10} {FormatBytes(s.TotalBytes),10} {s.EmptyFiles,6}");
        }
        return sb.ToString();
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        if (bytes >= GB) return $"{bytes / GB:F2} GB";
        if (bytes >= MB) return $"{bytes / MB:F1} MB";
        if (bytes >= KB) return $"{bytes / KB:F0} KB";
        return $"{bytes} B";
    }
}

public sealed class SymbolAuditReport
{
    public required string Symbol { get; init; }
    public int HourTickFiles { get; set; }
    public int DailyM1Files { get; set; }
    public int EmptyFiles { get; set; }
    public int UnexpectedFiles { get; set; }
    public long TotalBytes { get; set; }
    public DateTimeOffset? FirstHour { get; set; }
    public DateTimeOffset? LastHour { get; set; }
    public long ExpectedWeekdayHours { get; set; }
    public double CoverageRate { get; set; }
}
