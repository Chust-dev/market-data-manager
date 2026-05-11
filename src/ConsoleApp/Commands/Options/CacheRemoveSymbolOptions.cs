namespace HistoricalData.Commands.Options;

/// <summary>
/// Typed options for `cache remove-symbol`. Removes the symbol from all
/// three sections of <c>instruments.json</c> (<c>digits</c>,
/// <c>earliest</c>, <c>latest</c>). Does NOT delete cached
/// <c>.bi5</c> files on disk; the command output points the user at
/// <c>cache cleanup --instrument SYM</c> for that.
///
/// Prompts for confirmation by default since this mutates a config file;
/// <see cref="NonInteractive"/> (<c>--no-prompt</c>) skips the prompt
/// for scripted use.
/// </summary>
internal sealed record CacheRemoveSymbolOptions(
    string Instrument,
    bool NonInteractive,
    string InstrumentsConfigPath,
    bool Quiet)
{
    public static CacheRemoveSymbolOptions FromArgs(IReadOnlyDictionary<string, string> args) =>
        new(
            Instrument: CommonParsingHelpers.ParseInstrument(args),
            NonInteractive: CommonParsingHelpers.ParseNonInteractive(args),
            InstrumentsConfigPath: CommonParsingHelpers.ParseInstrumentsConfigPath(args),
            Quiet: CommonParsingHelpers.ParseQuiet(args));
}
