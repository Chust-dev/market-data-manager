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

            // Skip non-symbol directories that happen to live alongside symbol
            // folders (e.g. `Exports/` for MT5 outputs, or smoke-test tmp dirs).
            // A real symbol directory contains at least one 4-digit-year
            // subdirectory matching the cache layout. Structural check, not a
            // name match against instruments.json — symbols the user is
            // tracking but hasn't curated in the config yet still register.
            if (!LooksLikeSymbolDir(symbolDir))
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
                            // Per-(year, calendar-month) bucket so the optional
                            // `cache audit --by-year` / `--by-month` views can
                            // compute a finer-grained coverage grid without a
                            // second walk.
                            var key = (year, calMonth);
                            result.HourTickFilesByMonth[key] =
                                result.HourTickFilesByMonth.TryGetValue(key, out var prev) ? prev + 1 : 1;
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

    /// <summary>
    /// Cheap structural check used to filter non-symbol directories out of
    /// the audit walk. A real symbol folder contains at least one
    /// 4-digit-year subdirectory matching the
    /// pool/symbol/yyyy/MM/dd/file.bi5 layout; ad-hoc neighbours like
    /// <c>Exports/</c> or <c>MSBuildTempXXXXX/</c> don't, so they get
    /// skipped here rather than reported as bogus symbols with zero
    /// coverage. Short-circuits on the first match for cheapness.
    /// </summary>
    private static bool LooksLikeSymbolDir(string symbolDir)
    {
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(symbolDir);
        }
        catch
        {
            return false;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.Length == 4
                && int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                && year is >= 1990 and <= 2999)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Expected hour-tick files for a calendar month, counting Mon–Fri
    /// only (Saturday and Sunday are weekends in forex). Conservative:
    /// counts each weekday as 24 hours, slightly overestimating because
    /// Friday tails off around 21:00 UTC. Fully-cached months will render
    /// as ~98% rather than 100% for that reason; the relative comparison
    /// across months remains accurate.
    /// </summary>
    public static int ExpectedWeekdayHoursInMonth(int year, int month)
    {
        var hours = 0;
        var days = DateTime.DaysInMonth(year, month);
        for (var d = 1; d <= days; d++)
        {
            var dow = new DateTime(year, month, d).DayOfWeek;
            if (dow != DayOfWeek.Saturday && dow != DayOfWeek.Sunday)
            {
                hours += 24;
            }
        }
        return hours;
    }

    public static int ExpectedWeekdayHoursInYear(int year)
    {
        var total = 0;
        for (var m = 1; m <= 12; m++)
        {
            total += ExpectedWeekdayHoursInMonth(year, m);
        }
        return total;
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

    /// <summary>
    /// Year-level coverage grid: rows = symbols, columns = years observed.
    /// Each cell shows percentage of expected weekday hours that are cached
    /// in that year. Empty when no symbols have any hour-tick files.
    /// </summary>
    public string RenderByYear()
    {
        if (Symbols.Count == 0 || Symbols.All(s => s.HourTickFilesByMonth.Count == 0))
        {
            return "Year-by-year coverage: (no hour-tick files in scope)\n";
        }

        var allYears = Symbols
            .SelectMany(s => s.CoveredYears())
            .Distinct()
            .OrderBy(y => y)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("Year-by-year coverage:");
        sb.Append($"  {"Symbol",-10}");
        foreach (var y in allYears)
        {
            sb.Append($" {y,7}");
        }
        sb.AppendLine();

        foreach (var s in Symbols)
        {
            sb.Append($"  {s.Symbol,-10}");
            foreach (var y in allYears)
            {
                var actual = s.HourTickFilesIn(y);
                if (actual == 0)
                {
                    sb.Append($" {"-",7}");
                    continue;
                }
                var expected = PoolAuditor.ExpectedWeekdayHoursInYear(y);
                var pct = expected > 0 ? Math.Min(1.0, (double)actual / expected) : 0;
                sb.Append($" {pct,7:P1}");
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Month-level coverage grid, one block per symbol. Inside each block:
    /// rows = years, columns = Jan–Dec, cells = coverage percentage. Use
    /// `-` for months with zero cached files (typically the very start or
    /// very end of a symbol's history).
    /// </summary>
    public string RenderByMonth()
    {
        if (Symbols.Count == 0 || Symbols.All(s => s.HourTickFilesByMonth.Count == 0))
        {
            return "Month-by-month coverage: (no hour-tick files in scope)\n";
        }

        var sb = new StringBuilder();
        var monthNames = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        var first = true;
        foreach (var s in Symbols)
        {
            if (s.HourTickFilesByMonth.Count == 0)
            {
                continue;
            }

            if (!first) sb.AppendLine();
            first = false;

            sb.AppendLine($"{s.Symbol} month-by-month:");
            sb.Append($"  {"",-6}");
            foreach (var n in monthNames)
            {
                sb.Append($" {n,5}");
            }
            sb.AppendLine();

            foreach (var year in s.CoveredYears())
            {
                sb.Append($"  {year,-6}");
                for (var m = 1; m <= 12; m++)
                {
                    var actual = s.HourTickFilesIn(year, m);
                    if (actual == 0)
                    {
                        sb.Append($" {"-",5}");
                        continue;
                    }
                    var expected = PoolAuditor.ExpectedWeekdayHoursInMonth(year, m);
                    var pct = expected > 0 ? Math.Min(1.0, (double)actual / expected) : 0;
                    sb.Append($" {pct,5:P0}");
                }
                sb.AppendLine();
            }
        }

        return sb.ToString();
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

    /// <summary>
    /// Hour-tick file counts bucketed by (year, calendarMonth). Populated
    /// during the same walk that builds <see cref="HourTickFiles"/>; used
    /// only when the caller renders the by-year / by-month coverage grids.
    /// </summary>
    public Dictionary<(int Year, int Month), int> HourTickFilesByMonth { get; } = new();

    public int HourTickFilesIn(int year) =>
        HourTickFilesByMonth.Where(kv => kv.Key.Year == year).Sum(kv => kv.Value);

    public int HourTickFilesIn(int year, int month) =>
        HourTickFilesByMonth.TryGetValue((year, month), out var n) ? n : 0;

    /// <summary>Years for which at least one hour-tick file is cached.</summary>
    public IEnumerable<int> CoveredYears() =>
        HourTickFilesByMonth.Keys.Select(k => k.Year).Distinct().OrderBy(y => y);
}
