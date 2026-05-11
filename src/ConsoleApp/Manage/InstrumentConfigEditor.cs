using HistoricalData.Config;

namespace HistoricalData.Manage;

/// <summary>
/// Pure merge policy for adding and removing symbol entries in
/// <see cref="InstrumentConfig"/>. Lives outside the command classes so
/// the policy can be unit-tested without dragging in network / file-IO.
/// Mirrors the <see cref="DiscoveryMerge"/> pattern from BACKLOG #54.
/// </summary>
public static class InstrumentConfigEditor
{
    /// <summary>Maximum reasonable digits value. Dukascopy uses 3 for JPY pairs,
    /// 5 for major FX, 3 for XAUUSD; crypto can hit 8. Cap at 9 to catch typos
    /// without blocking legitimate exotic values.</summary>
    public const int MaxDigits = 9;

    /// <summary>
    /// Add a symbol to <see cref="InstrumentConfig.Digits"/>. Returns
    /// <see cref="AddSymbolResult.Ok"/> on success;
    /// <see cref="AddSymbolResult.AlreadyExists"/> if the symbol is already
    /// in the dict (unless <paramref name="force"/> is true, which upserts);
    /// <see cref="AddSymbolResult.InvalidSymbol"/> for empty / whitespace
    /// symbol names; <see cref="AddSymbolResult.InvalidDigits"/> for
    /// out-of-range digit values.
    ///
    /// Symbol is always uppercased before storage so callers don't have to
    /// pre-normalise.
    /// </summary>
    public static AddSymbolResult TryAdd(
        InstrumentConfig config,
        string symbol,
        int digits,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return AddSymbolResult.InvalidSymbol;
        }
        if (digits < 1 || digits > MaxDigits)
        {
            return AddSymbolResult.InvalidDigits;
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        if (config.Digits.ContainsKey(normalized) && !force)
        {
            return AddSymbolResult.AlreadyExists;
        }

        config.Digits[normalized] = digits;
        return AddSymbolResult.Ok;
    }

    /// <summary>
    /// Remove a symbol from all three sections of
    /// <see cref="InstrumentConfig"/>: <c>Digits</c>, <c>Earliest</c>,
    /// <c>Latest</c>. Returns a small record of which sections actually
    /// had an entry — useful for the user-facing summary
    /// ("removed from digits, earliest"). Symbol is uppercased before lookup
    /// so the call is case-insensitive against typical user input.
    ///
    /// Does NOT touch cached <c>.bi5</c> files on disk — that's
    /// <c>cache cleanup</c>'s job. The command-layer surfaces this in its
    /// output.
    /// </summary>
    public static RemoveSymbolResult Remove(InstrumentConfig config, string symbol)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return new RemoveSymbolResult(
                RemovedFromDigits: false,
                RemovedFromEarliest: false,
                RemovedFromLatest: false);
        }

        var normalized = symbol.Trim().ToUpperInvariant();
        var fromDigits = config.Digits.Remove(normalized);
        var fromEarliest = config.Earliest.Remove(normalized);
        var fromLatest = config.Latest.Remove(normalized);
        return new RemoveSymbolResult(fromDigits, fromEarliest, fromLatest);
    }
}

public enum AddSymbolResult
{
    Ok,
    AlreadyExists,
    InvalidSymbol,
    InvalidDigits
}

public sealed record RemoveSymbolResult(
    bool RemovedFromDigits,
    bool RemovedFromEarliest,
    bool RemovedFromLatest)
{
    public bool AnyRemoved => RemovedFromDigits || RemovedFromEarliest || RemovedFromLatest;
}
