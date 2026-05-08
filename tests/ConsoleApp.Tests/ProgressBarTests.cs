using HistoricalData.Utils;

namespace HistoricalData.Tests;

/// <summary>
/// Behavioral tests for the cross-pillar ProgressBar utility. These cover the
/// contract the widget must honor for any consumer (Downloader, Auditor,
/// BarExporter, TickExporter): no crashes on edge inputs, idempotent disposal,
/// thread-safe supplier polling, defensive against bad suppliers.
///
/// Visual rendering correctness (the actual bar/percent/ETA formatting) is
/// validated by manual smoke testing rather than fragile stdout-capture tests.
/// </summary>
public sealed class ProgressBarTests
{
    [Fact]
    public void CanInstantiateAndDispose_Quiet()
    {
        long counter = 0;
        using var pb = new ProgressBar("test", 10, () => counter, quiet: true);
        // No exception expected on this lifecycle.
    }

    [Fact]
    public void Complete_IsIdempotent()
    {
        long counter = 0;
        var pb = new ProgressBar("test", 10, () => counter, quiet: true);
        pb.Complete();
        pb.Complete();
        pb.Dispose();
        pb.Dispose();
    }

    [Fact]
    public void ZeroTotal_DoesNotCrash()
    {
        long counter = 0;
        using var pb = new ProgressBar("test", 0, () => counter, quiet: true);
    }

    [Fact]
    public void NegativeTotal_DoesNotCrash()
    {
        long counter = 0;
        using var pb = new ProgressBar("test", -1, () => counter, quiet: true);
    }

    [Fact]
    public void ConcurrentCounterReads_AreSafe()
    {
        long counter = 0;
        using var pb = new ProgressBar(
            "test", 1000,
            () => Interlocked.Read(ref counter),
            quiet: true);

        Parallel.For(0, 1000, _ => Interlocked.Increment(ref counter));
        Thread.Sleep(100);
        // Just confirms no crash from concurrent access between the
        // incrementing parallel loop and the polling background task.
    }

    [Fact]
    public void SupplierExceptions_AreSwallowed()
    {
        // A misbehaving supplier must not crash the polling task or block Dispose.
        using var pb = new ProgressBar(
            "test", 10,
            () => throw new InvalidOperationException("intentional"),
            quiet: true);
        Thread.Sleep(600); // let one poll cycle fire
    }

    [Fact]
    public void NullLabel_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ProgressBar(null!, 10, () => 0L, quiet: true));
    }

    [Fact]
    public void NullSupplier_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ProgressBar("test", 10, null!, quiet: true));
    }
}
