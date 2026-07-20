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

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 10_000);

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

    // ---------- WS2: adaptive warmup batching ----------

    [Fact]
    public void SlowBody_Effective_Batch_Is_One()
    {
        // A 2 s/sample body with default BatchSize = 8: the adaptive batch sizing should
        // shrink the effective batch to 1 (250 ms target / 2 s = 0.125, ceil = 1, clamped to
        // [1, 8] = 1). With batch = 1 the plateau is evaluated every sample, so patience (3)
        // is satisfied by sample 4 - but MinWarmup (8) still binds, so warmup settles at 8
        // samples, not 32. The 8 samples cost 16 s of warmup instead of 64 s.
        var detector = new WarmupPlateauDetector(AutoTuneOptions.Default, perSampleEstimateNs: 2_000_000_000.0);

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 10_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // MinWarmup (8) binds: the plateau would settle at 4 (Patience + 1 with batch = 1) but
        // the floor keeps the loop running until sample 8.
        Assert.Equal(8, resolvedAt);
    }

    [Fact]
    public void SlowBody_With_Low_MinWarmup_Settles_At_Patience_Plus_One()
    {
        // With MinWarmup lowered below (Patience + 1) * effectiveBatch, the plateau settles
        // at (Patience + 1) samples. For a 2 s body (effective batch = 1), Patience = 3, and
        // MinWarmup = 1: settles at 4 samples.
        var options = AutoTuneOptions.Default with { MinWarmup = 1 };
        var detector = new WarmupPlateauDetector(options, perSampleEstimateNs: 2_000_000_000.0);

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 10_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        Assert.Equal(4, resolvedAt);
    }

    [Fact]
    public void FastBody_Effective_Batch_Is_Configured_BatchSize()
    {
        // A 1 µs/sample body with default BatchSize = 8: 250 ms / 1 µs = 250_000, clamped to
        // [1, 8] = 8. The configured BatchSize is used unchanged; the plateau settles after
        // (Patience + 1) * 8 = 32 samples, the same as the pre-WS2 behavior.
        var detector = new WarmupPlateauDetector(AutoTuneOptions.Default, perSampleEstimateNs: 1_000.0);

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 10_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        Assert.Equal(32, resolvedAt);
    }

    [Fact]
    public void ZeroEstimate_FallsBack_To_Configured_BatchSize()
    {
        // perSampleEstimateNs = 0 (calibration did not run): the detector uses the configured
        // BatchSize unchanged. This is the K-pinned / setup-teardown / forced-GC path.
        var detector = new WarmupPlateauDetector(AutoTuneOptions.Default, perSampleEstimateNs: 0.0);

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 10_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        Assert.Equal(32, resolvedAt);
    }

    [Fact]
    public void MidSpeedBody_Effective_Batch_Is_Clamped_To_BatchSize()
    {
        // A 100 ms/sample body with default BatchSize = 8: 250 ms / 100 ms = 2.5, ceil = 3,
        // clamped to [1, 8] = 3. The plateau settles after (Patience + 1) * 3 = 12 samples.
        var detector = new WarmupPlateauDetector(AutoTuneOptions.Default, perSampleEstimateNs: 100_000_000.0);

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 10_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // (PlateauPatience + 1) * effectiveBatch = (3 + 1) * 3 = 12 samples.
        Assert.Equal(12, resolvedAt);
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
