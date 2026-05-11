namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache cleanup`. Removes zero-byte .bi5 files, orphan
/// .meta.json sidecars (no matching data file), and leftover .tmp files —
/// then prunes empty day/month/year directories.
///
/// IMPORTANT: this command is destructive by default. Running
/// <c>cache cleanup</c> with no flags <b>deletes</b> the matching files
/// (per the user-chosen safety default — opposite of <c>cache repair</c>'s
/// preview-by-default). Pass <see cref="DryRun"/> to preview the plan
/// first.
/// </summary>
internal sealed record CacheCleanupOptions(
    IReadOnlyCollection<string>? InstrumentFilter,
    string PoolPath,
    bool DryRun,
    bool Purge,
    bool Quiet)
{
    public static CacheCleanupOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            InstrumentFilter: CommonParsingHelpers.ParseInstrumentFilter(args),
            PoolPath: CommonParsingHelpers.ParsePoolPath(args),
            DryRun: CommonParsingHelpers.ParseDryRun(args),
            Purge: CommonParsingHelpers.ParsePurge(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
}
