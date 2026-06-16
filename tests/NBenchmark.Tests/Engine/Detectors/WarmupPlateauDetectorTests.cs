using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class WarmupPlateauDetectorTests
{
    [Fact]
    public void AlreadyWarmBody_SettlesAtFloorPlusPatience()
    {
        // Default options: MinWarmup 8, BatchSize 8, PlateauPatience 3.
        var detector = new WarmupPlateauDetector(AutoTuneOptions.Default);

        var resolvedAt = FeedConstantUntilResolved(detector, value: 100.0, cap: 10_000);

        // First batch (samples 1-8) sets the best and counts as improving; the next three
        // batches are non-improving, so warmup settles after 8 + 3 * 8 = 32 samples.
        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        Assert.Equal(32, resolvedAt);
    }

    [Fact]
    public void DecayingBody_SettlesAfterPlateau()
    {
        var detector = new WarmupPlateauDetector(AutoTuneOptions.Default);

        var resolvedAt = 0;

        for (var i = 1; i <= 5_000; i++)
        {
            // Decays linearly from ~400 ns down to a 150 ns floor by sample 50, then flat.
            var value = Math.Max(150.0, 400.0 - i * 5.0);

            if (detector.Feed(value))
            {
                resolvedAt = i;
                break;
            }
        }

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // Settles well after the decay flattens but far below the ceiling.
        Assert.InRange(resolvedAt, 50, 200);
    }

    [Fact]
    public void NeverStabilises_StopsAtCeiling()
    {
        var options = AutoTuneOptions.Default with { MinWarmup = 8, MaxWarmup = 40, BatchSize = 8, PlateauPatience = 3 };
        var detector = new WarmupPlateauDetector(options);

        var resolvedAt = 0;

        for (var i = 1; i <= 1_000; i++)
        {
            // Strictly decreasing forever: every batch improves, so it never plateaus.
            if (detector.Feed(1_000.0 - i * 10.0))
            {
                resolvedAt = i;
                break;
            }
        }

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.MaxCeiling, detector.StopReason);
        Assert.Equal(40, resolvedAt);
    }

    private static int FeedConstantUntilResolved(WarmupPlateauDetector detector, double value, int cap)
    {
        for (var i = 1; i <= cap; i++)
        {
            if (detector.Feed(value))
                return i;
        }

        throw new InvalidOperationException("Detector did not resolve.");
    }
}
