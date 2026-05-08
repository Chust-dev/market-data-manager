using System.Diagnostics;

namespace HistoricalData.Utils;

/// <summary>
/// Cross-pillar progress bar for long-running operations. Renders an in-place
/// updating line on Console.Out showing label, percent, current/total, and ETA.
///
/// Usage:
///     using var pb = new ProgressBar("EURUSD", totalHours, () => counter, quiet: false);
///     // ... work that increments counter ...
/// (Dispose flushes the final line.)
///
/// Generic by design — accepts any <see cref="Func{long}"/> for the current value,
/// so each consumer (Downloader, Auditor, BarExporter, TickExporter) can plug in
/// its own counter source without coupling the widget to pillar-specific types.
/// The widget itself has no pillar-specific dependencies.
///
/// Thread safety: the supplier is invoked from a background polling thread.
/// Consumers should ensure their counter reads are atomic (a 64-bit `long` field
/// read on .NET is atomic on 64-bit runtimes; for paranoia, use
/// <see cref="System.Threading.Interlocked.Read(ref long)"/>).
///
/// Falls back to occasional milestone lines on non-TTY (output redirected to file).
/// <c>quiet=true</c> produces no output at all.
/// </summary>
public sealed class ProgressBar : IDisposable
{
    private const int BarWidth = 20;
    private const int PollIntervalMs = 500;

    private readonly string _label;
    private readonly long _total;
    private readonly Func<long> _currentSupplier;
    private readonly bool _quiet;
    private readonly bool _isTty;
    private readonly Stopwatch _stopwatch;
    private readonly CancellationTokenSource _cts;
    private readonly Task _renderTask;
    private readonly object _renderLock = new();

    private int _lastReportedMilestone = -1;
    private bool _completed;

    public ProgressBar(string label, long total, Func<long> currentSupplier, bool quiet)
    {
        _label = label ?? throw new ArgumentNullException(nameof(label));
        _currentSupplier = currentSupplier ?? throw new ArgumentNullException(nameof(currentSupplier));
        _total = total;
        _quiet = quiet;
        _isTty = !Console.IsOutputRedirected;
        _stopwatch = Stopwatch.StartNew();
        _cts = new CancellationTokenSource();
        _renderTask = Task.Run(RenderLoopAsync);
    }

    private async Task RenderLoopAsync()
    {
        if (_quiet || _total <= 0)
        {
            return;
        }

        var token = _cts.Token;
        while (!token.IsCancellationRequested)
        {
            Render(SafeCurrent(), final: false);
            try
            {
                await Task.Delay(PollIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private long SafeCurrent()
    {
        try
        {
            return _currentSupplier();
        }
        catch
        {
            // Don't let a misbehaving supplier crash the polling task.
            return 0;
        }
    }

    private void Render(long current, bool final)
    {
        if (_quiet || _total <= 0)
        {
            return;
        }

        var capped = Math.Min(Math.Max(current, 0), _total);
        var pct = (double)capped / _total;
        var elapsed = _stopwatch.Elapsed;

        if (_isTty)
        {
            RenderTty(capped, pct, elapsed);
            if (final)
            {
                Console.WriteLine();
            }
        }
        else
        {
            RenderNonTty(capped, pct, elapsed, final);
        }
    }

    private void RenderTty(long current, double pct, TimeSpan elapsed)
    {
        lock (_renderLock)
        {
            var bar = BuildBar(pct);
            var eta = ComputeEta(current, elapsed);
            var line = $"{_label}  {bar}  {pct:P1}  {current:N0}/{_total:N0}  ETA {eta}";
            // Trailing spaces clear any leftover characters from a previously longer line.
            Console.Write($"\r{line}    ");
        }
    }

    private void RenderNonTty(long current, double pct, TimeSpan elapsed, bool final)
    {
        var milestone = (int)(pct * 10);
        if (milestone == _lastReportedMilestone && !final)
        {
            return;
        }
        _lastReportedMilestone = milestone;

        var eta = ComputeEta(current, elapsed);
        Console.WriteLine($"{_label}  {pct:P0}  {current:N0}/{_total:N0}  ETA {eta}");
    }

    private static string BuildBar(double pct)
    {
        var filled = (int)Math.Round(pct * BarWidth);
        filled = Math.Clamp(filled, 0, BarWidth);
        return "[" + new string('█', filled) + new string('░', BarWidth - filled) + "]";
    }

    private string ComputeEta(long current, TimeSpan elapsed)
    {
        if (current <= 0 || elapsed.TotalSeconds < 1)
        {
            return "calculating...";
        }

        var rate = current / elapsed.TotalSeconds;
        var remaining = _total - current;
        if (rate <= 0 || remaining <= 0)
        {
            return "0s";
        }

        var seconds = remaining / rate;
        return FormatDuration(TimeSpan.FromSeconds(seconds));
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h{ts.Minutes:D2}m";
        if (ts.TotalMinutes >= 1) return $"{ts.Minutes}m{ts.Seconds:D2}s";
        return $"{(int)ts.TotalSeconds}s";
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }
        _completed = true;

        _cts.Cancel();
        try
        {
            _renderTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best effort.
        }

        Render(SafeCurrent(), final: true);
    }

    public void Dispose()
    {
        Complete();
        _cts.Dispose();
    }
}
