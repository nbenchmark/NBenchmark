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
        // warmup open until a full quiet period has elapsed since the count last moved.
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MinWarmupTime = TimeSpan.FromTicks(1), // 100 ns floor, met at sample 1
            RequireJitQuiescence = true,
        };
        var detector = new WarmupPlateauDetector(options);

        // JIT count: 0 (baseline at sample 1), then 5 from sample 2 onward - so the count changes once,
        // at 200 ns, and never again. The quiet period is clamped to the 100 ns floor.
        var resolvedAt = FeedUntilResolved(
            detector, perOp: 100.0, elapsedNs: 100.0, jit: i => i <= 1 ? 0 : 5, cap: 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // Sample 2 is plateau-ready but the count just moved (0 ns of quiet); by sample 3 the change is
        // 100 ns in the past, satisfying the quiet period -> settle.
        Assert.Equal(3, resolvedAt);

        // The delta is reported for the diagnostic: baseline 0 at the first boundary, 5 at the last.
        Assert.Equal(5, detector.JitCompiledDelta);
        Assert.True(detector.TimeFloorMet);
    }

    [Fact]
    public void JitGate_Requires_The_Quiet_Period_To_Elapse_After_The_Last_Change()
    {
        // The failure the old per-batch rule could not catch: the count moves at a batch boundary, then
        // the *next* boundary sees no change. A per-batch delta reads zero there and settles
        // immediately; the quiet-interval rule keeps warming until enough time has actually passed.
        // Quiet period 500 ns against a 500 ns floor (the clamp keeps them equal here).
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MinWarmupTime = TimeSpan.FromTicks(5), // 500 ns floor -> quiet period clamped to 500 ns
            JitQuietPeriod = TimeSpan.FromMilliseconds(50),
            RequireJitQuiescence = true,
        };
        var detector = new WarmupPlateauDetector(options);

        // The count moves once, at sample 3 (300 ns) - before the floor is reached - then stays put.
        var resolvedAt = FeedUntilResolved(
            detector, perOp: 100.0, elapsedNs: 100.0, jit: i => i < 3 ? 1 : 2, cap: 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);

        // Change at 300 ns + 500 ns of required quiet = 800 ns, reached at sample 8 (and well inside the
        // 2,000 ns deactivation threshold). A per-batch delta rule would have settled at sample 5: the
        // batch at the floor saw no compilation, so its delta was zero.
        Assert.Equal(8, resolvedAt);
    }

    [Fact]
    public void JitQuietPeriod_Is_Clamped_To_The_Time_Floor()
    {
        // A quiet period longer than the floor must not become the binding floor: with the count never
        // moving, warmup still ends at the floor rather than at the (much larger) quiet period.
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MinWarmupTime = TimeSpan.FromTicks(3), // 300 ns floor
            JitQuietPeriod = TimeSpan.FromSeconds(10), // absurdly long; clamped to 300 ns
            RequireJitQuiescence = true,
        };
        var detector = new WarmupPlateauDetector(options);

        var resolvedAt = FeedUntilResolved(
            detector, perOp: 100.0, elapsedNs: 100.0, jit: _ => 7, cap: 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        // 300 ns floor / 100 ns per sample = 3 samples; the plateau is ready at sample 2.
        Assert.Equal(3, resolvedAt);
    }

    [Fact]
    public void MaxCeiling_Reports_TimeFloorMet_False_When_Warmup_Was_Cut_Short()
    {
        // A body far too fast to accumulate the floor within the sample ceiling. This is the silent
        // failure the caller now warns about: warmup exits on the ceiling with the body potentially
        // still on pre-tier-1 code, and TimeFloorMet is how the caller detects it.
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MaxWarmup = 20,
            MinWarmupTime = TimeSpan.FromMilliseconds(100), // unreachable: 20 samples x 1 ns = 20 ns
            RequireJitQuiescence = false,
        };
        var detector = new WarmupPlateauDetector(options);

        var resolvedAt = FeedUntilResolved(detector, perOp: 1.0, elapsedNs: 1.0, jit: _ => 0, cap: 1_000);

        Assert.Equal(20, resolvedAt);
        Assert.Equal(WarmupStopReason.MaxCeiling, detector.StopReason);
        Assert.False(detector.TimeFloorMet);
    }

    [Fact]
    public void EffectiveBatchSize_Reports_The_Shrunk_Batch_For_A_Slow_Body()
    {
        // The caller reads EffectiveBatchSize to sample the JIT counter only at boundaries, so it has
        // to reflect the slow-body shrink rather than the configured value.
        var options = AutoTuneOptions.Default with { BatchSize = 8 };

        // 250 ms target batch / 100 ms per sample -> ceil(2.5) = 3, under the configured 8.
        var slow = new WarmupPlateauDetector(options, perSampleEstimateNs: 100_000_000.0);
        Assert.Equal(3, slow.EffectiveBatchSize);

        // A fast body is unaffected: the scaled value is clamped to the configured BatchSize.
        var fast = new WarmupPlateauDetector(options, perSampleEstimateNs: 1_000.0);
        Assert.Equal(8, fast.EffectiveBatchSize);
    }

    [Fact]
    public void Unsampled_Jit_Count_Does_Not_Disturb_The_Gate()
    {
        // The caller passes -1 on non-boundary samples. With BatchSize 1 every sample is a boundary, so
        // force the issue directly: a stream of -1 must behave exactly like "never changed", i.e. the
        // gate collapses to the time floor rather than blocking forever.
        var options = AutoTuneOptions.Default with
        {
            BatchSize = 1,
            PlateauPatience = 1,
            MinWarmup = 1,
            MinWarmupTime = TimeSpan.FromTicks(3), // 300 ns floor
            RequireJitQuiescence = true,
        };
        var detector = new WarmupPlateauDetector(options);

        var resolvedAt = FeedUntilResolved(detector, perOp: 100.0, elapsedNs: 100.0, jit: _ => -1L, cap: 1_000);

        Assert.True(detector.Resolved);
        Assert.Equal(WarmupStopReason.Settled, detector.StopReason);
        Assert.Equal(3, resolvedAt);
        Assert.Equal(0, detector.JitCompiledDelta);
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

    // ── Warmup curve and JIT-tier signals ──

    [Fact]
    public void Curve_Records_One_Point_Per_Completed_Batch()
    {
        var options = PlateauOnly with { BatchSize = 4, PlateauPatience = 2, MinWarmup = 1 };
        var detector = new WarmupPlateauDetector(options);

        // No batch has completed yet, so there is nothing to plot.
        Assert.Empty(detector.Curve);

        var resolvedAt = FeedUntilResolved(detector, perOp: 250.0, elapsedNs: 250.0, jit: _ => 0, cap: 1_000);

        Assert.Equal(resolvedAt / 4, detector.Curve.Length);
        Assert.All(detector.Curve, v => Assert.Equal(250.0, v));
        Assert.Equal(4, detector.CurveSampleInterval);
    }

    [Fact]
    public void Curve_Captures_The_Tier_Up_Decay()
    {
        // The point of retaining the curve: a body that starts slow in tier-0 code and speeds up as
        // the JIT promotes it must show that drop, otherwise there is nothing to visualise.
        var options = PlateauOnly with { BatchSize = 4, PlateauPatience = 3, MinWarmup = 1 };
        var detector = new WarmupPlateauDetector(options);

        for (var i = 1; i <= 1_000; i++)
        {
            // 1000 ns in tier-0 for the first batch, then 100 ns once promoted. The promotion has to
            // land within the first batch or two for the curve to record it: the plateau rule would
            // otherwise settle during the flat tier-0 stretch and warmup would end before the
            // speed-up was ever observed — precisely the "measured mid-tier-up" case the JIT
            // quiescence gate and MinWarmupTime floor exist to prevent.
            var perOp = i <= 4 ? 1000.0 : 100.0;
            if (detector.Feed(perOp, perOp, 0))
                break;
        }

        var curve = detector.Curve;
        Assert.True(curve.Length >= 2);
        Assert.Equal(1000.0, curve[0]);
        Assert.Equal(100.0, curve[^1]);
    }

    [Fact]
    public void JitLastChangeAtNs_Marks_Where_Compilation_Stopped()
    {
        // This is the closest thing to a tier-up landing marker: the point in warmup after which the
        // JIT compiled nothing more.
        var options = PlateauOnly with { BatchSize = 1, PlateauPatience = 100, MinWarmup = 1 };
        var detector = new WarmupPlateauDetector(options);

        // Count climbs for the first 5 batches, then holds. Batch 1 sets the baseline, so the last
        // observed change is at batch 5 — 5 x 100 ns of accumulated warmup.
        for (var i = 1; i <= 10; i++)
            detector.Feed(100.0, 100.0, i <= 5 ? i : 5);

        Assert.Equal(500.0, detector.JitLastChangeAtNs);
        Assert.Equal(1000.0, detector.WarmupElapsedNs);
        // Baseline captured at batch 1 (count 1), last count 5.
        Assert.Equal(4, detector.JitCompiledDelta);
    }

    [Fact]
    public void JitQuiescenceAchieved_Is_False_When_Compilation_Ran_To_The_End()
    {
        // MinWarmupTime non-zero and quiescence required, so the gate is live. The compiled-method
        // count changes on the final batch, meaning measurement would start with compilation still
        // in flight.
        var options = AutoTuneOptions.Default with
        {
            MinWarmupTime = TimeSpan.FromTicks(10_000), // 1 ms
            RequireJitQuiescence = true,
            BatchSize = 1,
            PlateauPatience = 100,
            MinWarmup = 1,
        };
        var detector = new WarmupPlateauDetector(options);

        for (var i = 1; i <= 10; i++)
            detector.Feed(100.0, 100.0, i);

        Assert.False(detector.JitQuiescenceAchieved);
    }

    [Fact]
    public void JitQuiescenceAchieved_Is_True_When_The_Gate_Is_Not_Required()
    {
        var detector = new WarmupPlateauDetector(PlateauOnly);

        FeedUntilResolved(detector, perOp: 100.0, elapsedNs: 100.0, jit: i => i, cap: 1_000);

        Assert.True(detector.JitQuiescenceAchieved);
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
