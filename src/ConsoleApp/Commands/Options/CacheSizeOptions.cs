using HistoricalData.Manage;

namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache size`. Sums file sizes per symbol (and
/// optionally per year), sorts by a chosen column, renders as a text
/// table to stdout.
///
/// CSV / file output is a planned follow-up (see BACKLOG #23 notes);
/// for now this is text-only.
/// </summary>
internal sealed record CacheSizeOptions(
    IReadOnlyCollection<string>? InstrumentFilter,
    string PoolPath,
    bool ByYear,
    CacheSizeSort SortBy,
    bool Quiet)
{
    public static CacheSizeOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            InstrumentFilter: CommonParsingHelpers.ParseInstrumentFilter(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            ByYear: CommonParsingHelpers.ParseByYear(args),
            SortBy: CommonParsingHelpers.ParseCacheSizeSort(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
}
