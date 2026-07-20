using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class WarmupPlateauDetectorTests
{
    // The plateau/batch tests isolate the plateau rule from the warmup time floor and
    // JIT-quiescence gate (both exercised in dedicated tests below): MinWarmupTime = 0 disables the
    // floor, which also disables the JIT gate, so settling depends only on the plateau + batch rule.
    private static readonly AutoTuneOptions PlateauOnly =
        AutoTuneOptions.Default with { MinWarmupTime = TimeSpan.Zero, RequireJitQuiescence = false };

    [Fact]
    public void AlreadyWarmBody_SettlesAtFloorPlusPatience()
    {
        // Default options: MinWarmup 8, BatchSize 8, PlateauPatience 3.
        var detector = new WarmupPlateauDetector(PlateauOnly);

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
        var detector = new WarmupPlateauDetector(PlateauOnly);

        var resolvedAt = 0;

        for (var i = 1; i <= 5_000; i++)
        {
            // Decays linearly from ~400 ns down to a 150 ns floor by sample 50, then flat.
            var value = Math.Max(150.0, 400.0 - i * 5.0);

            if (detector.Feed(value, value, 0))
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
        var options = PlateauOnly with { MinWarmup = 8, MaxWarmup = 40, BatchSize = 8, PlateauPatience = 3 };
        var detector = new WarmupPlateauDetector(options);

        var resolvedAt = 0;

        for (var i = 1; i <= 1_000; i++)
        {
            // Strictly decreasing forever: every batch improves, so it never plateaus.
            var value = 1_000.0 - i * 10.0;

            if (detector.Feed(value, value, 0))
            {
                resolvedAt = i;
                break;
            }
        }

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.MaxCeiling, detector.StopReason);
        Assert.Equal(40, resolvedAt);
    }

    // ---------- Adaptive warmup batching for slow bodies ----------

    [Fact]
    public void SlowBody_Effective_Batch_Is_One()
    {
        // A 2 s/sample body with default BatchSize = 8: the adaptive batch sizing should
        // shrink the effective batch to 1 (250 ms target / 2 s = 0.125, ceil = 1, clamped to
        // [1, 8] = 1). With batch = 1 the plateau is evaluated every sample, so patience (3)
        // is satisfied by sample 4 - but MinWarmup (8) still binds, so warmup settles at 8
        // samples, not 32. The 8 samples cost 16 s of warmup instead of 64 s.
        var detector = new WarmupPlateauDetector(PlateauOnly, perSampleEstimateNs: 2_000_000_000.0);

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
        var options = PlateauOnly with { MinWarmup = 1 };
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
        // (Patience + 1) * 8 = 32 samples (the default batch-based behavior).
        var detector = new WarmupPlateauDetector(PlateauOnly, perSampleEstimateNs: 1_000.0);

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
        var detector = new WarmupPlateauDetector(PlateauOnly, perSampleEstimateNs: 0.0);

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
        var detector = new WarmupPlateauDetector(PlateauOnly, perSampleEstimateNs: 100_000_000.0);

        var resolvedAt = FeedConstantUntilResolved(detector, 100.0, 10_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // (PlateauPatience + 1) * effectiveBatch = (3 + 1) * 3 = 12 samples.
        Assert.Equal(12, resolvedAt);
    }

    // ---------- Warmup time floor ----------

    [Fact]
    public void TimeFloor_Blocks_Settling_Until_MinWarmupTime_Reached()
    {
        // batch=1, patience=1, MinWarmup=1 -> the plateau rule is satisfied at sample 2. Each sample
        // reports 100 ns elapsed; MinWarmupTime = 1000 ns means the floor is met only at sample 10.
        // The JIT gate is off, so the time floor alone delays settling from sample 2 to sample 10.
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MinWarmupTime = TimeSpan.FromTicks(10), // 10 ticks x 100 ns/tick = 1000 ns
            RequireJitQuiescence = false,
        };
        var detector = new WarmupPlateauDetector(options);

        var resolvedAt = FeedUntilResolved(detector, perOp: 100.0, elapsedNs: 100.0, jit: _ => 0, cap: 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // 1000 ns floor / 100 ns per sample = 10 samples; plateau was ready at sample 2.
        Assert.Equal(10, resolvedAt);
    }

    // ---------- JIT-quiescence gate ----------

    [Fact]
    public void JitGate_Blocks_While_Jit_Compiling_Then_Settles_When_Quiet()
    {
        // The plateau is ready and the (tiny) time floor is met immediately, but the JIT gate holds
        // warmup open while the compiled-method count keeps rising, and releases it on the first
        // batch with a zero delta.
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MinWarmupTime = TimeSpan.FromTicks(1), // 100 ns floor, met at sample 1
            RequireJitQuiescence = true,
        };
        var detector = new WarmupPlateauDetector(options);

        // JIT count: 0 (baseline at sample 1), 5 at sample 2 (delta 5 -> blocks), 5 thereafter
        // (delta 0 -> quiescent).
        var resolvedAt = FeedUntilResolved(
            detector, perOp: 100.0, elapsedNs: 100.0, jit: i => i <= 1 ? 0 : 5, cap: 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // Sample 2 is plateau-ready but the JIT delta of 5 blocks; sample 3 has delta 0 -> settle.
        Assert.Equal(3, resolvedAt);
    }

    [Fact]
    public void JitGate_Deactivates_After_Four_Times_MinWarmupTime()
    {
        // The JIT never goes quiet (the count rises every batch), but the gate must not block
        // forever: it deactivates once warmup has run 4 x MinWarmupTime, letting the plateau settle.
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MinWarmupTime = TimeSpan.FromTicks(1), // 100 ns floor -> deactivate at 400 ns
            RequireJitQuiescence = true,
        };
        var detector = new WarmupPlateauDetector(options);

        var resolvedAt = FeedUntilResolved(
            detector, perOp: 100.0, elapsedNs: 100.0, jit: i => i * 10L, cap: 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // Deactivation at 4 x 100 ns = 400 ns; warmupElapsed reaches 400 ns at sample 4.
        Assert.Equal(4, resolvedAt);
    }

    [Fact]
    public void LastBatchMeanPerOp_Reports_Most_Recent_Batch_Mean()
    {
        // The recalibration path reads LastBatchMeanPerOp as the warm per-op estimate; verify it
        // tracks the most recently completed batch rather than staying at its initial zero.
        var options = PlateauOnly with { BatchSize = 4, PlateauPatience = 2, MinWarmup = 1 };
        var detector = new WarmupPlateauDetector(options);

        Assert.Equal(0.0, detector.LastBatchMeanPerOp);

        FeedUntilResolved(detector, perOp: 250.0, elapsedNs: 250.0, jit: _ => 0, cap: 1_000);

        Assert.Equal(250.0, detector.LastBatchMeanPerOp);
    }

    private static int FeedConstantUntilResolved(WarmupPlateauDetector detector, double value, int cap)
        => FeedUntilResolved(detector, value, value, _ => 0, cap);

    private static int FeedUntilResolved(
        WarmupPlateauDetector detector, double perOp, double elapsedNs, Func<int, long> jit, int cap)
    {
        for (var i = 1; i <= cap; i++)
        {
            if (detector.Feed(perOp, elapsedNs, jit(i)))
                return i;
        }

        throw new InvalidOperationException("Detector did not resolve.");
    }
}
