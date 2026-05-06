namespace HistoricalData.Export;

public sealed class SummaryReport
{
    public long Ticks { get; set; }
    public long Bars { get; set; }
    public long M1FallbackBars { get; set; }
    public long FallbackBarsSkipped { get; set; }
    public long DuplicateTicksDropped { get; set; }
    public long GapRepairBarsAdded { get; set; }
    public long GapRepairBarsSkipped { get; set; }
    public long ValidationChecked { get; set; }
    public long ValidationMismatches { get; set; }
    public long MissingHours { get; set; }
    public long HoursProcessed { get; set; }

    public void Print()
    {
        Console.WriteLine("Summary:");
        Console.WriteLine($"  Hours processed: {HoursProcessed}");
        Console.WriteLine($"  Missing hours:   {MissingHours}");
        Console.WriteLine($"  Ticks:           {Ticks}");
        Console.WriteLine($"  Bars:            {Bars}");
        Console.WriteLine($"  M1 fallback bars:{M1FallbackBars}");
        Console.WriteLine($"  Fallback skipped:{FallbackBarsSkipped}");
        Console.WriteLine($"  Tick deduped:    {DuplicateTicksDropped}");
        Console.WriteLine($"  Gap repair add:  {GapRepairBarsAdded}");
        Console.WriteLine($"  Gap repair skip: {GapRepairBarsSkipped}");
        Console.WriteLine($"  Validate checked:{ValidationChecked}");
        Console.WriteLine($"  Validate mismatch:{ValidationMismatches}");
    }
}
