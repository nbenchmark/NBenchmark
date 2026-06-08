using System.Diagnostics;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     End-to-end sanity check that the measurement engine's output aligns with
///     ground truth. Unlike mean-based assertions (which absorb all scheduler
///     preemption spikes), the minimum sample is stable: a CPU-bound busy-wait
///     has a hard floor, and preemption only ever adds time. This catches unit
///     errors, wiring bugs, or a broken measurement loop - classes of bugs the
///     deterministic statistical tests cannot detect.
/// </summary>
public class TimingSanityTests
{
    private static void BusyWait(double microseconds)
    {
        var ticks = (long)(microseconds * Stopwatch.Frequency / 1_000_000.0);
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < ticks)
        {
        }
    }

    [Fact]
    public void Engine_MinimumSample_Is_Near_Known_BusyWait_Floor()
    {
        const double targetMicros = 5_000.0; // 5 ms
        const double targetNanos = targetMicros * 1_000.0;

        var outcome = BenchmarkRunner.Instance.Run(
            "busywait",
            () => BusyWait(targetMicros),
            new RunSpec
            {
                Options = new MeasurementOptions
                {
                    WarmupIterations = 3,
                    Iterations = 15,
                    OutlierMode = OutlierMode.None,
                    MeasureAllocations = false,
                },
            });

        // The fastest iteration cannot beat the busy-wait floor, and preemption
        // only adds time. A wide upper bound (10x) still catches gross unit/wiring
        // errors (e.g. ns reported as ms) without flaking under CI throttling.
        // Lower bound (70%) accounts for timer overhead, loop slack, and Stopwatch
        // resolution on slow runners.
        Assert.InRange(outcome.Result.Min, targetNanos * 0.7, targetNanos * 10.0);
    }
}
