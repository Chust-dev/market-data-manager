using System.Globalization;
using System.Text;

namespace HistoricalData.Manage;

/// <summary>
/// Measures disk footprint of the .bi5 cache, bucketed by symbol and
/// optionally by year. Pure file-size sums — no decompression, no hashing,
/// no network. Lives in the Manage pillar alongside PoolAuditor; uses the
/// same `LooksLikeSymbolDir` structural filter so non-symbol neighbours
/// (Exports/, MSBuildTempXXX/, etc.) are silently skipped.
///
/// Two-phase API for consistency with the rest of the Manage pillar:
/// <see cref="PlanSymbols"/> enumerates the candidate symbol directories
/// (fast); <see cref="MeasureAll"/> walks each one and sums file sizes.
/// </summary>
public sealed class CacheSizer
{
    private const string Bi5Suffix = ".bi5";

    private readonly string _poolPath;

    public CacheSizer(string poolPath)
    {
        _poolPath = poolPath ?? throw new ArgumentNullException(nameof(poolPath));
    }

    /// <summary>
    /// Convenience: enumerate and measure in a single call.
    /// </summary>
    public CacheSizeReport Measure(IReadOnlyCollection<string>? symbolFilter = null) =>
        MeasureAll(PlanSymbols(symbolFilter));

    /// <summary>
    /// Pass 1: walk the pool root, return symbol directories that pass the
    /// filter and the structural year-subdir check.
    /// </summary>
    public IReadOnlyList<string> PlanSymbols(IReadOnlyCollection<string>? symbolFilter = null)
    {
        var result = new List<string>();
        if (!Directory.Exists(_poolPath))
        {
            return result;
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

            if (!LooksLikeSymbolDir(symbolDir))
            {
                continue;
            }

            result.Add(symbolDir);
        }

        return result;
    }

    /// <summary>
    /// Pass 2: sum file sizes per symbol and per (symbol, year).
    /// </summary>
    public CacheSizeReport MeasureAll(IReadOnlyList<string> symbolDirs)
    {
        var report = new CacheSizeReport
        {
            PoolPath = _poolPath,
            PoolExists = Directory.Exists(_poolPath)
        };

        foreach (var symbolDir in symbolDirs)
        {
            var symbol = Path.GetFileName(symbolDir);
            var entry = new SymbolSize { Symbol = symbol };
            MeasureSymbol(symbolDir, entry);
            // Skip a symbol entirely if its walk yielded no .bi5 files —
            // mirrors the audit's "honest empty" behaviour without polluting
            // the size table with zero rows.
            if (entry.Files > 0)
            {
                report.Symbols.Add(entry);
            }
        }

        return report;
    }

    private static void MeasureSymbol(string symbolDir, SymbolSize entry)
    {
        foreach (var yearDir in Directory.EnumerateDirectories(symbolDir))
        {
            if (!int.TryParse(Path.GetFileName(yearDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            {
                continue;
            }

            foreach (var monthDir in Directory.EnumerateDirectories(yearDir))
            {
                if (!int.TryParse(Path.GetFileName(monthDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                foreach (var dayDir in Directory.EnumerateDirectories(monthDir))
                {
                    if (!int.TryParse(Path.GetFileName(dayDir), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        continue;
                    }

                    foreach (var file in Directory.EnumerateFiles(dayDir, "*" + Bi5Suffix))
                    {
                        long size;
                        try
                        {
                            size = new FileInfo(file).Length;
                        }
                        catch
                        {
                            continue; // unreadable file — skip silently rather than blow up the report
                        }

                        entry.Bytes += size;
                        entry.Files++;

                        if (!entry.ByYear.TryGetValue(year, out var yearEntry))
                        {
                            yearEntry = new YearSize { Year = year };
                            entry.ByYear[year] = yearEntry;
                        }
                        yearEntry.Bytes += size;
                        yearEntry.Files++;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Same structural check used by <see cref="PoolAuditor"/>: skip
    /// non-symbol neighbours (no 4-digit-year subdirectory underneath).
    /// Duplicated rather than shared so the two pillars stay independent.
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
}

public enum CacheSizeSort
{
    Size,
    Symbol,
    Year,
    Files
}

public sealed class SymbolSize
{
    public required string Symbol { get; init; }
    public long Bytes { get; set; }
    public int Files { get; set; }
    public Dictionary<int, YearSize> ByYear { get; } = new();
}

public sealed class YearSize
{
    public required int Year { get; init; }
    public long Bytes { get; set; }
    public int Files { get; set; }
}

public sealed class CacheSizeReport
{
    public required string PoolPath { get; init; }
    public bool PoolExists { get; init; }
    public List<SymbolSize> Symbols { get; } = new();

    public long TotalBytes => Symbols.Sum(s => s.Bytes);
    public int TotalFiles => Symbols.Sum(s => s.Files);
    public int YearBucketCount =>
        Symbols.SelectMany(s => s.ByYear.Keys).Distinct().Count();

    /// <summary>
    /// Render the per-symbol summary, sorted per <paramref name="sortBy"/>.
    /// `Year` falls back to `Size` here because there's no year column to
    /// sort by in the per-symbol view.
    /// </summary>
    public string Render(CacheSizeSort sortBy)
    {
        var sb = new StringBuilder();
        if (!PoolExists)
        {
            sb.AppendLine($"Pool path does not exist: {PoolPath}");
            return sb.ToString();
        }

        sb.AppendLine($"Pool: {PoolPath}");
        sb.AppendLine($"Total size: {FormatBytes(TotalBytes)} across {Symbols.Count} symbol(s)");

        if (Symbols.Count == 0)
        {
            return sb.ToString();
        }

        var sorted = SortSymbols(sortBy);
        sb.AppendLine();
        sb.AppendLine($"  {"Symbol",-10} {"Size",10} {"Files",10}");
        foreach (var s in sorted)
        {
            sb.AppendLine($"  {s.Symbol,-10} {FormatBytes(s.Bytes),10} {s.Files,10:N0}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render the per-(symbol, year) long-format breakdown. Sort interpretation:
    ///   * Size / Files: descending by that column;
    ///   * Symbol: ascending alphabetic, then ascending year inside each symbol;
    ///   * Year: ascending year, then ascending symbol inside each year.
    /// </summary>
    public string RenderByYear(CacheSizeSort sortBy)
    {
        var sb = new StringBuilder();
        if (!PoolExists)
        {
            sb.AppendLine($"Pool path does not exist: {PoolPath}");
            return sb.ToString();
        }

        sb.AppendLine($"Pool: {PoolPath}");
        sb.AppendLine($"Total size: {FormatBytes(TotalBytes)} across {Symbols.Count} symbol(s), {YearBucketCount} year-bucket(s)");

        if (Symbols.Count == 0)
        {
            return sb.ToString();
        }

        var rows = SortByYearRows(sortBy);
        sb.AppendLine();
        sb.AppendLine($"  {"Symbol",-10} {"Year",6} {"Size",10} {"Files",10}");
        foreach (var (symbol, year) in rows)
        {
            sb.AppendLine($"  {symbol.Symbol,-10} {year.Year,6} {FormatBytes(year.Bytes),10} {year.Files,10:N0}");
        }
        return sb.ToString();
    }

    private IEnumerable<SymbolSize> SortSymbols(CacheSizeSort sortBy) =>
        sortBy switch
        {
            CacheSizeSort.Symbol => Symbols.OrderBy(s => s.Symbol, StringComparer.OrdinalIgnoreCase),
            CacheSizeSort.Files => Symbols.OrderByDescending(s => s.Files),
            // Year sort falls back to size — there's no year column to sort by in
            // the per-symbol view, and largest-first is the most useful default.
            CacheSizeSort.Year => Symbols.OrderByDescending(s => s.Bytes),
            _ => Symbols.OrderByDescending(s => s.Bytes), // Size (default)
        };

    private IEnumerable<(SymbolSize symbol, YearSize year)> SortByYearRows(CacheSizeSort sortBy)
    {
        var flat = Symbols.SelectMany(s => s.ByYear.Values.Select(y => (symbol: s, year: y))).ToList();
        return sortBy switch
        {
            CacheSizeSort.Symbol => flat
                .OrderBy(t => t.symbol.Symbol, StringComparer.OrdinalIgnoreCase)
                .ThenBy(t => t.year.Year),
            CacheSizeSort.Year => flat
                .OrderBy(t => t.year.Year)
                .ThenBy(t => t.symbol.Symbol, StringComparer.OrdinalIgnoreCase),
            CacheSizeSort.Files => flat.OrderByDescending(t => t.year.Files),
            _ => flat.OrderByDescending(t => t.year.Bytes), // Size (default)
        };
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
