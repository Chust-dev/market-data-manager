namespace HistoricalData.Utils;

public static class ConsolePrompts
{
    public static AppOptions FillMissing(AppOptions options, IReadOnlyList<string> availableInstruments)
    {
        var defaults = AppOptions.Defaults;

        var selectionMode = PromptOption(
            "Instrument selection",
            new[] { "A) Single instrument", "B) Multiple instruments", "C) All available instruments" },
            string.IsNullOrWhiteSpace(options.Instruments) ? "A" : "B",
            "A");

        var instrument = options.Instrument;
        var instruments = options.Instruments;
        if (selectionMode == "A")
        {
            instrument = Prompt("Instrument", options.Instrument, defaults.Instrument).ToUpperInvariant();
            instruments = string.Empty;
        }
        else if (selectionMode == "B")
        {
            instruments = PromptInstruments(availableInstruments, options);
            var first = instruments.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            instrument = string.IsNullOrWhiteSpace(first) ? defaults.Instrument : first.ToUpperInvariant();
        }
        else
        {
            instruments = "all";
            instrument = defaults.Instrument;
        }

        var start = PromptDateTime("Start (ISO 8601)", options.Start, defaults.Start);
        var end = PromptDateTime("End (ISO 8601)", options.End, defaults.End);
        var timeframe = Prompt("Timeframe", options.Timeframe, defaults.Timeframe);

        var mode = PromptOption("Download mode", new[] { "A) Tick->M1", "B) Direct M1" },
            options.DownloadMode == DownloadMode.TickToM1 ? "A" : "B", "A");
        var downloadMode = mode == "B" ? DownloadMode.DirectM1 : DownloadMode.TickToM1;

        var format = PromptOption("Output format", new[] { "A) CSV", "B) CSV+HST" }, "B", "B");
        var outputFormat = format == "A" ? OutputFormat.CsvOnly : OutputFormat.CsvHst;

        var offsetText = Prompt("UTC offset (+hh:mm)", options.UtcOffset.ToString(), defaults.UtcOffset.ToString());
        var offset = TimeSpanParser.TryParse(offsetText, defaults.UtcOffset);

        var dataPool = Prompt("Data pool path", options.DataPoolPath, defaults.DataPoolPath);
        var output = Prompt("Output path", options.OutputPath, defaults.OutputPath);
        var refreshCache = PromptBool("Refresh cache", options.RefreshCache);
        var recentRefreshDays = PromptInt("Recent refresh days", options.RecentRefreshDays);
        var verifyChecksum = PromptBool("Verify checksums", options.VerifyChecksum);
        var deduplicateTicks = PromptBool("Deduplicate ticks", options.DeduplicateTicks);
        var skipFallbackIfTicked = PromptBool("Skip fallback overlap", options.SkipFallbackIfTicked);
        var repairGaps = PromptBool("Repair gaps", options.RepairGaps);
        var validateM1 = PromptBool("Validate M1", options.ValidateM1);
        var validationTolerancePoints = PromptInt("Validation tolerance (points)", options.ValidationTolerancePoints);
        var useSessionCalendar = PromptBool("Use session calendar", options.UseSessionCalendar);
        var sessionConfigPath = options.SessionConfigPath;
        if (useSessionCalendar)
        {
            sessionConfigPath = Prompt("Session config path", options.SessionConfigPath, defaults.SessionConfigPath);
        }

        return options with
        {
            Instrument = instrument,
            Instruments = instruments,
            Start = start,
            End = end,
            Timeframe = timeframe,
            DownloadMode = downloadMode,
            OutputFormat = outputFormat,
            UtcOffset = offset,
            DataPoolPath = dataPool,
            OutputPath = output,
            RefreshCache = refreshCache,
            RecentRefreshDays = recentRefreshDays,
            VerifyChecksum = verifyChecksum,
            DeduplicateTicks = deduplicateTicks,
            SkipFallbackIfTicked = skipFallbackIfTicked,
            RepairGaps = repairGaps,
            ValidateM1 = validateM1,
            ValidationTolerancePoints = validationTolerancePoints,
            UseSessionCalendar = useSessionCalendar,
            SessionConfigPath = sessionConfigPath
        };
    }

    private static string PromptInstruments(IReadOnlyList<string> availableInstruments, AppOptions options)
    {
        if (availableInstruments.Count == 0)
        {
            return options.Instruments;
        }

        Console.WriteLine("Available instruments:");
        for (var i = 0; i < availableInstruments.Count; i++)
        {
            Console.WriteLine($"  {i + 1}) {availableInstruments[i]}");
        }

        var current = string.IsNullOrWhiteSpace(options.Instruments) ? options.Instrument : options.Instruments;
        Console.Write("Select indexes or symbols (comma-separated, or 'all')");
        Console.Write($" [{current}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return current;
        }

        var value = input.Trim();
        if (value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return "all";
        }

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, out var index) && index >= 1 && index <= availableInstruments.Count)
            {
                set.Add(availableInstruments[index - 1]);
                continue;
            }

            set.Add(token.ToUpperInvariant());
        }

        return string.Join(',', set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
    }

    private static string Prompt(string label, string current, string fallback)
    {
        Console.Write($"{label} [{current}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.IsNullOrWhiteSpace(current) ? fallback : current;
        }

        return input.Trim();
    }

    private static DateTimeOffset PromptDateTime(string label, DateTimeOffset current, DateTimeOffset fallback)
    {
        Console.Write($"{label} [{current:O}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return current;
        }

        return DateTimeParser.TryParse(input, current == default ? fallback : current);
    }

    private static string PromptOption(string label, string[] options, string current, string fallback)
    {
        Console.WriteLine(label + ":");
        foreach (var option in options)
        {
            Console.WriteLine("  " + option);
        }
        Console.Write($"Select [{current}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.IsNullOrWhiteSpace(current) ? fallback : current;
        }

        return input.Trim().ToUpperInvariant();
    }

    private static bool PromptBool(string label, bool current)
    {
        var currentText = current ? "Y" : "N";
        Console.Write($"{label} (Y/N) [{currentText}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return current;
        }

        return input.Trim().Equals("Y", StringComparison.OrdinalIgnoreCase)
               || input.Trim().Equals("YES", StringComparison.OrdinalIgnoreCase)
               || input.Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase)
               || input.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
               || input.Trim().Equals("ON", StringComparison.OrdinalIgnoreCase);
    }

    private static int PromptInt(string label, int current)
    {
        Console.Write($"{label} [{current}]: ");
        var input = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(input))
        {
            return current;
        }

        return int.TryParse(input.Trim(), out var parsed) ? parsed : current;
    }
}
