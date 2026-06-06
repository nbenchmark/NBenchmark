using System.Diagnostics;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Coarse end-to-end timing sanity checks. These assert that the engine's
///     reported timings land near an independently measured ground truth for a
///     CPU-bound busy-wait body. They are inherently sensitive to scheduler noise,
///     so they use generous tolerances and are tagged so a loaded CI agent can skip
///     them with <c>dotnet test --filter "Category!=Timing"</c>.
/// </summary>
[Trait("Category", "Timing")]
public class TimingSanityTests
{
    // A CPU-bound busy-wait is far more reproducible than Task.Delay (which is
    // bounded below by the OS timer granularity, ~15 ms on Windows).
    private static void BusyWait(double microseconds)
    {
        var ticks = (long)(microseconds * Stopwatch.Frequency / 1_000_000.0);
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetTimestamp() - start < ticks)
        {
        }
    }

    [Fact]
    public void Engine_Reports_Mean_Near_Known_BusyWait_Duration()
    {
        const double targetMicros = 2_000.0; // 2 ms
        const double targetNanos = targetMicros * 1_000.0;

        var outcome = MeasurementEngine.MeasureSync(
            "busywait",
            () => BusyWait(targetMicros),
            new MeasurementOptions
            {
                WarmupIterations = 5,
                Iterations = 30,
                OutlierMode = OutlierMode.RemoveTop5Percent,
                MeasureAllocations = false,
            });

        // Within ±40% of the target. The mean can only run long (preemption),
        // never materially short, so this is a generous two-sided band.
        Assert.InRange(outcome.Result.Mean, targetNanos * 0.6, targetNanos * 1.4);
    }

    [Fact]
    public void Engine_Mean_Tracks_Manual_Stopwatch_Loop()
    {
        const double targetMicros = 1_000.0; // 1 ms
        const int iterations = 50;

        // Manual ground-truth measurement of the identical body.
        for (var w = 0; w < 5; w++)
            BusyWait(targetMicros);

        var manualStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
            BusyWait(targetMicros);
        var manualMeanNanos =
            Stopwatch.GetElapsedTime(manualStart).TotalNanoseconds / iterations;

        var outcome = MeasurementEngine.MeasureSync(
            "busywait",
            () => BusyWait(targetMicros),
            new MeasurementOptions
            {
                WarmupIterations = 5,
                Iterations = iterations,
                OutlierMode = OutlierMode.None,
                MeasureAllocations = false,
            });

        // The engine's per-iteration mean should track the manual loop within
        // ±30% — the residual is scheduling jitter, not a systematic bias.
        Numerics.AssertRelativeClose(manualMeanNanos, outcome.Result.Mean, 0.30);
    }
}
