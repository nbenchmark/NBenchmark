using NBenchmark.Engine;
using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class AdaptiveLoopTests
{
    [Fact]
    public void ExplicitCounts_RunExactly_And_Report_ExplicitStops()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 2,
            WarmupIterations = 3,
            Iterations = 5,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
        };

        // 2000 ns per sample at K = 2 -> 1000 ns per op.
        var clock = new ScriptedClock(2000.0);

        var result = RunSync(() => bodyCalls++, options, clock);

        Assert.Equal(5, result.PerOpTimings.Length);
        Assert.All(result.PerOpTimings, t => Assert.Equal(1000.0, t));
        Assert.Equal(2, result.Diagnostic.OpsPerSample);
        Assert.Equal(5, result.Diagnostic.ResolvedSamples);
        Assert.Equal(WarmupStopReason.ExplicitCount, result.Diagnostic.WarmupStop);
        Assert.Equal(SampleStopReason.ExplicitCount, result.Diagnostic.SampleStop);
        Assert.Empty(result.Warnings);
        Assert.Equal((3 + 5) * 2L, result.Diagnostic.TotalBodyInvocations);
        Assert.Equal((3 + 5) * 2, bodyCalls);
    }

    [Fact]
    public void AutoWarmup_Discards_Prefix_From_Measured_Stats()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1, // pin K so no calibration runs
            WarmupIterations = null, // auto warmup
            Iterations = 10, // explicit measured count
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // Isolate the plateau rule from the warmup time floor and JIT gate (both covered by
            // dedicated tests): with a scripted 1000 ns/sample body the 100 ms floor would otherwise
            // hold warmup open to MaxWarmup instead of settling on the plateau.
            AutoTune = AutoTuneOptions.Default with { MinWarmupTime = TimeSpan.Zero, RequireJitQuiescence = false },
        };

        // A flat signal settles the plateau rule at its floor.
        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => bodyCalls++, options, clock);

        // Constant signal: plateau settles at MinWarmup + PlateauPatience * BatchSize = 8 + 3 * 8 = 32.
        Assert.Equal(32, result.ResolvedWarmup);
        Assert.Equal(WarmupStopReason.Settled, result.Diagnostic.WarmupStop);
        Assert.Empty(result.Warnings);

        // Only the 10 measured samples reach the stats; the 32-sample warmup prefix is discarded.
        Assert.Equal(10, result.PerOpTimings.Length);
        Assert.Equal(10, result.Diagnostic.ResolvedSamples);
        Assert.Equal(SampleStopReason.ExplicitCount, result.Diagnostic.SampleStop);
        Assert.Equal(32 + 10, bodyCalls);
    }

    [Fact]
    public void AutoSamples_Stop_At_Ci_Target()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0, // no warmup
            Iterations = null, // auto sample count -> CI detector
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
        };

        // Zero-variance signal -> CI half-width is 0, so the target is met at the first cadence point.
        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.Equal(0, result.ResolvedWarmup);
        Assert.Equal(WarmupStopReason.ExplicitCount, result.Diagnostic.WarmupStop);

        // The first cadence multiple (BatchSize 8) at or past MinSamples (30) is 32.
        Assert.Equal(32, result.PerOpTimings.Length);
        Assert.Equal(32, result.Diagnostic.ResolvedSamples);
        Assert.Equal(SampleStopReason.CiTargetMet, result.Diagnostic.SampleStop);
        Assert.Equal(0.0, result.Diagnostic.AchievedRelativeCiWidth, 10);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void EligibleFastBody_Calibrates_OpsPerSample_Above_One()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = null, // auto-calibrate
            WarmupIterations = 0,
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // Isolate Phase A calibration, and pin the 1 µs target this test's scripted timings assume
            // (the default is now 10 µs).
            AutoTune = AutoTuneOptions.Default with { EnableJitterCalibration = false, TargetSampleDurationNs = 1_000 },
        };

        // Calibration probes each candidate K = 1, 2, 4 five times and feeds the *fastest* reading
        // to the search. Each step opens with a 9999 ns cold-start spike that the minimum discards;
        // the steady readings are 250, 500 and 1000 ns. The target is 1000 ns, so K resolves at 4 on
        // the 1000 ns step. (With the old single-sample logic the 9999 ns spike on the very first
        // K = 1 probe would have cleared the target and frozen K at 1.) Measured samples then read
        // 1000 ns, which is 250 ns per op at K = 4.
        var clock = new ScriptedClock(call => call switch
        {
            // K = 1 step
            0 => 9999.0,
            1 or 2 or 3 or 4 => 250.0,

            // K = 2 step
            5 => 9999.0,
            6 or 7 or 8 or 9 => 500.0,

            // K = 4 step
            10 => 9999.0,
            11 or 12 or 13 or 14 => 1000.0,

            // measured samples at K = 4
            _ => 1000.0,
        });

        var result = RunSync(() => bodyCalls++, options, clock);

        Assert.Equal(4, result.Diagnostic.OpsPerSample);
        Assert.Equal(3, result.PerOpTimings.Length);
        Assert.All(result.PerOpTimings, t => Assert.Equal(250.0, t));

        // Calibration body calls: 5 probes each at K = 1, 2, 4 -> 5 * (1 + 2 + 4) = 35;
        // measured: 3 * 4 = 12.
        Assert.Equal(35 + 12, bodyCalls);

        // TotalBodyInvocations counts every phase, including the 35 calibration probes.
        Assert.Equal(bodyCalls, result.Diagnostic.TotalBodyInvocations);
    }

    [Fact]
    public void SlowBody_ShortCircuits_Calibration_After_First_Probe()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = null, // auto-calibrate
            WarmupIterations = 0,
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // Isolate Phase A; pin the 1 µs target so the short-circuit ratio below is unambiguous.
            AutoTune = AutoTuneOptions.Default with { EnableJitterCalibration = false, TargetSampleDurationNs = 1_000 },
        };

        // Each sample spans 10 ms (10,000,000 ns) - 10,000x the pinned TargetSampleDurationNs
        // (1,000 ns), well above the SlowBodyShortCircuitFactor of 1,000. The doubling search
        // would settle on K = 1 after a single probe, so running the remaining 4 probes is pure
        // waste (today this step alone burns 40 ms of in-body time for a 10 ms body).
        var clock = new ScriptedClock(10_000_000.0);

        var result = RunSync(() => bodyCalls++, options, clock);

        // K resolves to 1 (the slow body clears the target on the first probe).
        Assert.Equal(1, result.Diagnostic.OpsPerSample);

        // Calibration ran exactly 1 probe at K = 1 (today: 5). With K = 1, each probe invokes the
        // body once, so calibration contributes 1 body call; the 3 pinned measured samples add 3
        // more. Body calls: 1 calibration + 0 warmup + 3 measured = 4 (today: 5 + 0 + 3 = 8).
        Assert.Equal(4, bodyCalls);
        Assert.Equal(4, result.Diagnostic.TotalBodyInvocations);

        // The 3 measured samples each read 10,000,000 ns / K = 10,000,000 ns per op.
        Assert.Equal(3, result.PerOpTimings.Length);
        Assert.All(result.PerOpTimings, t => Assert.Equal(10_000_000.0, t));
    }

    [Fact]
    public void PostWarmupRecalibration_Bumps_K_When_Warm_Body_Faster_Than_Cold()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = null, // auto-calibrate against cold speed
            WarmupIterations = null, // auto warmup (recalibration only runs in this path)
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // 10 µs target; isolate from the warmup time floor / JIT gate so warmup settles on the
            // plateau at 32 samples.
            AutoTune = AutoTuneOptions.Default with
            {
                EnableJitterCalibration = false,
                TargetSampleDurationNs = 10_000,
                MinWarmupTime = TimeSpan.Zero,
                RequireJitQuiescence = false,
            },
        };

        // Cold calibration: 5 probes at 10 µs each -> a K = 1 sample already spans the target, so K
        // resolves to 1. Then the warm body runs 100x faster (100 ns/op), so the warm sample spans
        // only 100 ns « half the 10 µs target and K is recalibrated to next-pow2(10000/100) = 128.
        var clock = new ScriptedClock(call => call < 5 ? 10_000.0 : 100.0);

        var result = RunSync(() => bodyCalls++, options, clock);

        Assert.Equal(1, result.Diagnostic.InitialOpsPerSample); // cold K
        Assert.Equal(128, result.Diagnostic.OpsPerSample); // recalibrated warm K
        Assert.Equal(3, result.PerOpTimings.Length);

        // 5 calibration (K=1) + 32 warmup (K=1) + 1 untimed recalibration sample (K=128) + 3 measured
        // (K=128) = 5 + 32 + 128 + 384 = 549. The 128-invocation jump proves the untimed sample ran
        // at the new K to warm the larger batch's cache/branch state.
        Assert.Equal(549, bodyCalls);
        Assert.Equal(549, result.Diagnostic.TotalBodyInvocations);
    }

    [Fact]
    public void PostWarmupRecalibration_Skipped_When_Warm_Sample_Near_Target()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = null,
            WarmupIterations = null,
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                EnableJitterCalibration = false,
                TargetSampleDurationNs = 10_000,
                MinWarmupTime = TimeSpan.Zero,
                RequireJitQuiescence = false,
            },
        };

        // The warm body is no faster than the cold code (constant 10 µs/op): the warm sample already
        // spans the full target, above the half-target trigger, so K stays 1 and no recalibration
        // occurs. InitialOpsPerSample is null when the loop did not recalibrate.
        var clock = new ScriptedClock(10_000.0);

        var result = RunSync(() => bodyCalls++, options, clock);

        Assert.Equal(1, result.Diagnostic.OpsPerSample);
        Assert.Null(result.Diagnostic.InitialOpsPerSample);
        Assert.Equal(3, result.PerOpTimings.Length);

        // 5 calibration + 32 warmup + 3 measured, all at K = 1, with no untimed recalibration sample.
        Assert.Equal(5 + 32 + 3, bodyCalls);
    }

    [Fact]
    public void Setup_Makes_Body_Ineligible_For_Calibration_So_K_Stays_One()
    {
        var bodyCalls = 0;
        var setupCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = null, // auto, but an iteration setup disqualifies calibration
            WarmupIterations = 0,
            Iterations = 5,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
        };

        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => bodyCalls++, options, clock, () => setupCalls++);

        Assert.Equal(1, result.Diagnostic.OpsPerSample);
        Assert.Equal(5, result.PerOpTimings.Length);
        Assert.Equal(5, bodyCalls);
        Assert.Equal(5, setupCalls); // one setup per measured sample (warmup = 0)
    }

    [Fact]
    public void Sample_Timings_And_Allocations_Are_Divided_By_OpsPerSample()
    {
        const int opsPerSample = 4;
        const int blockBytes = 8192;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = opsPerSample,
            WarmupIterations = 1, // JIT the body before measuring so allocation deltas are clean
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = true,
        };

        // 4000 ns per sample at K = 4 -> 1000 ns per op.
        var clock = new ScriptedClock(4000.0);

        // Allocate a known block per invocation; the sink escapes so the allocation is not elided.
        byte[]? sink = null;
        var result = RunSync(() => sink = new byte[blockBytes], options, clock);
        GC.KeepAlive(sink);

        Assert.Equal(3, result.PerOpTimings.Length);
        Assert.All(result.PerOpTimings, t => Assert.Equal(1000.0, t));

        Assert.NotNull(result.PerOpAllocations);
        Assert.Equal(3, result.PerOpAllocations!.Length);

        // Each sample times K invocations together, so the recorded per-op allocation must reflect a
        // single call (~one block), not K of them. A band below K x blockBytes proves the divide-by-K
        // happened: an implementation that forgot it would report ~K x blockBytes per op.
        Assert.All(result.PerOpAllocations, a =>
        {
            Assert.True(a >= blockBytes,
                $"per-op allocation {a} should be at least one block ({blockBytes})");

            Assert.True(a < 2L * blockBytes,
                $"per-op allocation {a} should be ~one block, not K x block (~{(long)opsPerSample * blockBytes}); divide-by-K missing?");
        });
    }

    [Fact]
    public void WallClock_Cap_Stops_A_Non_Converging_Measurement_And_Adds_Warning()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = null, // auto -> would otherwise collect at least MinSamples (30)
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromTicks(50), CapGraceFactor = 1.0 }, // 5000 ns
        };

        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        // Accumulated sample time crosses the 5000 ns cap on the 5th 1000 ns sample, far below MinSamples.
        // CapGraceFactor = 1.0 disables the grace path, so the loop stops at the base cap.
        Assert.Equal(SampleStopReason.WallClockCap, result.Diagnostic.SampleStop);
        Assert.Equal(5, result.PerOpTimings.Length);
        Assert.Single(result.Warnings);
        Assert.Contains("wall-clock tuning cap", result.Warnings[0]);
        Assert.Contains("--max-tuning-time", result.Warnings[0]);
    }

    [Fact]
    public void WallClock_Cap_Grace_Continues_Past_Base_Cap_And_Reports_GraceCapExhausted()
    {
        // The grace-ceiling feature: when the wall-clock cap fires below MinSamples, the grace path
        // keeps sampling up to MaxTuningTime * CapGraceFactor instead of stopping on a dangerously
        // under-sampled result. This trades extra runtime for enough samples to compute meaningful
        // statistics; without grace this body would stop at 10 samples with unreliable margins.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = null, // auto -> CI detector; MinSamples (30) never reached under the cap
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // Base cap 10000 ns (10 samples), grace ceiling 20000 ns (20 samples). Both below the
            // MinSamples floor of 30, so the grace ceiling is what stops the loop.
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromTicks(100), CapGraceFactor = 2.0 },
        };

        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        // The base cap fires at sample 10 but grace carries sampling to the 20000 ns ceiling (20
        // samples) - double what the base cap alone would have collected.
        Assert.Equal(SampleStopReason.GraceCapExhausted, result.Diagnostic.SampleStop);
        Assert.Equal(20, result.PerOpTimings.Length);
        Assert.Single(result.Warnings);
        Assert.Contains("grace ceiling", result.Warnings[0]);
        Assert.Contains("only 20 samples", result.Warnings[0]);
        Assert.Contains("unreliable", result.Warnings[0]);
    }

    [Fact]
    public void WallClock_Cap_Grace_Reaches_MinSamples_Then_Stops_WallClockCap()
    {
        // When the grace ceiling is high enough to let the loop reach MinSamples, the result is a
        // normal WallClockCap stop with the standard cap warning - not GraceCapExhausted, and no
        // "unreliable" flag. This proves grace extends sampling only as far as it needs to.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = null,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // Base cap 25000 ns (25 samples), grace ceiling 50000 ns. MinSamples (30) sits between
            // them, so grace carries the loop to exactly 30 samples and then stops at the base cap.
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromTicks(250), CapGraceFactor = 2.0 },
        };

        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.Equal(SampleStopReason.WallClockCap, result.Diagnostic.SampleStop);
        Assert.Equal(30, result.PerOpTimings.Length); // grace extended past the base-cap's 25 samples to MinSamples
        Assert.Single(result.Warnings);
        Assert.DoesNotContain("grace ceiling", result.Warnings[0]);
        Assert.DoesNotContain("unreliable", result.Warnings[0]);
    }

    [Fact]
    public async Task RunAsync_WallClock_Cap_Grace_Reports_GraceCapExhausted()
    {
        // Mirror of the sync grace test: the async overload carries the identical grace logic, so
        // it must produce the same GraceCapExhausted stop and sample count.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = null,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromTicks(100), CapGraceFactor = 2.0 },
        };

        var clock = new ScriptedClock(1000.0);
        var spec = new RunSpec { Options = options };

        var result = await AdaptiveLoop.RunAsync(
            "bench",
            () => Task.CompletedTask,
            spec,
            clock,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            CancellationToken.None);

        Assert.Equal(SampleStopReason.GraceCapExhausted, result.Diagnostic.SampleStop);
        Assert.Equal(20, result.PerOpTimings.Length);
        Assert.Single(result.Warnings);
        Assert.Contains("grace ceiling", result.Warnings[0]);
        Assert.Contains("unreliable", result.Warnings[0]);
    }

    [Fact]
    public void WallClock_Cap_With_Pinned_Iterations_Reports_Collected_Of_Pinned()
    {
        // When the user pinned --iterations and the cap fires before that count is reached, the
        // warning must not suggest "pinning --iterations" (they already did). Instead it should
        // report how many of the requested samples were collected and suggest a lower pinned
        // count or a larger cap.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 1_000, // pinned count, far above what the cap allows
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromTicks(50), CapGraceFactor = 1.0 }, // 5000 ns
        };

        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        // The 5000 ns cap fires after 5 of the pinned 1000 iterations.
        Assert.Equal(SampleStopReason.WallClockCap, result.Diagnostic.SampleStop);
        Assert.Equal(5, result.PerOpTimings.Length);
        Assert.Single(result.Warnings);

        // The pinned-iterations message names both counts and points at --iterations, not
        // "pinning --iterations". Use the raw value (5) directly so the assertion is robust to
        // culture-dependent thousands separators in :N0 formatting.
        var warning = result.Warnings[0];
        Assert.Contains("wall-clock tuning cap", warning);
        Assert.Contains("after collecting 5 of the pinned", warning);
        Assert.Contains("iterations", warning);
        Assert.Contains("--max-tuning-time", warning);
        Assert.Contains("reducing --iterations", warning);
        Assert.DoesNotContain("pinning --iterations", warning);
    }

    [Fact]
    public void WallClock_Cap_During_Auto_Warmup_Adds_Warning()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = null, // auto warmup
            Iterations = 0, // measurement phase exits immediately on explicit count
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromTicks(50), CapGraceFactor = 1.0 }, // 5000 ns
        };

        // Constant 1000 ns/op signal will never settle to the plateau rule's satisfaction.
        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.Equal(WarmupStopReason.WallClockCap, result.Diagnostic.WarmupStop);
        Assert.True(result.ResolvedWarmup >= 0);
        Assert.Single(result.Warnings);
        // The warning names the calibration+warmup budget share (default 40%), not the full cap -
        // warmup stops at its share (2 µs of the 5 µs cap here), so saying "stopped at the 5 µs cap"
        // would misstate when it actually stopped.
        Assert.Contains("Warmup exhausted its calibration+warmup budget", result.Warnings[0]);
        Assert.Contains("40%", result.Warnings[0]);
        Assert.Contains("--max-tuning-time", result.Warnings[0]);
    }

    [Fact]
    public async Task RunAsync_Mirrors_Sync_For_Explicit_Counts()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 2,
            Iterations = 4,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
        };

        var clock = new ScriptedClock(1000.0);
        var spec = new RunSpec { Options = options };

        var result = await AdaptiveLoop.RunAsync(
            "bench",
            () =>
            {
                bodyCalls++;
                return Task.CompletedTask;
            },
            spec,
            clock,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            CancellationToken.None);

        Assert.Equal(4, result.PerOpTimings.Length);
        Assert.Equal(2, result.ResolvedWarmup);
        Assert.Equal(SampleStopReason.ExplicitCount, result.Diagnostic.SampleStop);
        Assert.Empty(result.Warnings);
        Assert.Equal(2 + 4, bodyCalls); // warmup 2 + measured 4, at K = 1
    }

    [Fact]
    public void WallClock_Cap_During_Calibration_Adds_Calibration_Warning_And_Skips_Warmup()
    {
        var bodyCalls = 0;

        // Pin the calibrator so K is small and the search exhausts the cap while resolving K.
        // Setting TargetSampleDurationNs very high makes the calibrator probe K = 1, 2, 4, 8 ...
        // until the cap is hit before reaching the target.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = null, // auto-calibrate K
            WarmupIterations = null, // auto warmup (skipped if calibration is capped)
            Iterations = 5, // explicit measured count
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                MaxTuningTime = TimeSpan.FromTicks(50), // 5000 ns cap
                CapGraceFactor = 1.0,
                TargetSampleDurationNs = 100_000_000, // unreachable target so calibration probes several Ks
            },
        };

        // Each sample takes 1000 ns; cap = 5000 ns so calibration exhausts it within ~5 probes.
        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => bodyCalls++, options, clock);

        // Both phases hit the shared cap: calibration exhausted the budget before settling,
        // warmup is skipped (one budget check on the way in still trips), and the first
        // measurement sample also trips the cap.
        Assert.Equal(WarmupStopReason.WallClockCap, result.Diagnostic.WarmupStop);
        Assert.Equal(SampleStopReason.WallClockCap, result.Diagnostic.SampleStop);
        Assert.Single(result.Warnings);
        Assert.Contains("Calibration exhausted its calibration+warmup budget", result.Warnings[0]);
        Assert.Contains("--max-tuning-time", result.Warnings[0]);
        Assert.DoesNotContain("Warmup exhausted", result.Warnings[0]);
        Assert.True(result.ResolvedWarmup > 0);
    }

    [Fact]
    public void WallClock_Cap_Warning_Shows_The_Cap_Not_The_Elapsed_Time()
    {
        // Auto warmup with a constant signal would normally settle at 32 samples
        // (MinWarmup 8 + PlateauPatience 3 * BatchSize 8). Push MinWarmup + Patience high
        // so the plateau would only settle well past the cap, and pair with a small cap
        // so warmup exhausts the budget before plateau settles.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = null, // auto warmup
            Iterations = 0, // measurement exits immediately on explicit count
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                MaxTuningTime = TimeSpan.FromTicks(50), // 5000 ns cap
                MinWarmup = 1_000,
                PlateauPatience = 1_000,
            },
        };

        var clock = new ScriptedClock(1000.0);
        var result = RunSync(() => { }, options, clock);

        Assert.Equal(WarmupStopReason.WallClockCap, result.Diagnostic.WarmupStop);
        Assert.Single(result.Warnings);

        // The cap label (5.00 µs for a 5000 ns cap) appears in the warning; the elapsed text
        // is not shown.
        Assert.Contains("5.00 µs", result.Warnings[0]);
        Assert.Contains("Warmup exhausted its calibration+warmup budget", result.Warnings[0]);
    }

    [Fact]
    public void MaxCeiling_With_Unmet_CiTarget_Adds_Warning()
    {
        // Noisy signal alternating between two values: the CI half-width never shrinks to the
        // target, so the loop runs to the MaxSamples ceiling without converging.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = null, // auto -> CI detector
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                MinSamples = 4,
                MaxSamples = 20,
                BatchSize = 2,
                CiTarget = 0.001, // unreachable for the noisy signal
                EnableJitterCalibration = false,
            },
        };

        // Alternating 1000/2000 ns -> mean 1500, stddev ~500, CV ~33%. The CI half-width at
        // n=20 is t*se/mean ~= 2.09 * 500/sqrt(20) / 1500 ~= 15.6%, far above 0.1%.
        var clock = new ScriptedClock(call => call % 2 == 0 ? 1000.0 : 2000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.Equal(SampleStopReason.MaxCeiling, result.Diagnostic.SampleStop);
        Assert.Equal(20, result.PerOpTimings.Length);
        Assert.True(result.Diagnostic.AchievedRelativeCiWidth > options.AutoTune.CiTarget);
        Assert.Single(result.Warnings);
        Assert.Contains("sample ceiling", result.Warnings[0]);
        Assert.Contains("20", result.Warnings[0]);
        Assert.Contains("--max-samples", result.Warnings[0]);
    }

    [Fact]
    public void MaxCeiling_With_Met_CiTarget_Adds_No_Warning()
    {
        // A quiet signal that meets the CI target exactly at the MaxSamples ceiling should not
        // emit the MaxCeiling warning: the loop converged, it just happened to land on the
        // boundary. The warning is for the *unmet target* case, not the ceiling per se.
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = null,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                MinSamples = 4,
                MaxSamples = 32,
                BatchSize = 8,
                CiTarget = 0.025,
                EnableJitterCalibration = false,
            },
        };

        // Zero-variance signal: CI half-width is 0, met at the first cadence point (32).
        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        // The CI target is met at 32 (the first cadence multiple >= MinSamples), so the loop
        // stops with CiTargetMet, not MaxCeiling - no warning either way. This test guards the
        // boundary: a body that converges exactly at the ceiling is not warned.
        Assert.Equal(SampleStopReason.CiTargetMet, result.Diagnostic.SampleStop);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void JitterCalibration_Runs_By_Default_And_Reports_Metric()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                JitterCalibrationSamples = 8,
                JitterCalibrationWorkPerSample = 16,
            },
        };

        // Constant clock: zero-variance jitter probe -> metric is 0, no switch.
        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.NotNull(result.Diagnostic.JitterMetric);
        Assert.Equal(0.0, result.Diagnostic.JitterMetric!.Value, 10);
        Assert.False(result.Diagnostic.OutlierDetectorSwitched);
        Assert.Null(result.EffectiveOutlierDetector);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void JitterCalibration_Skipped_When_Disabled_Reports_Null_Metric()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { EnableJitterCalibration = false },
        };

        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.Null(result.Diagnostic.JitterMetric);
        Assert.False(result.Diagnostic.OutlierDetectorSwitched);
        Assert.Null(result.EffectiveOutlierDetector);
    }

    [Fact]
    public void JitterCalibration_AutoSwitches_To_Mad_When_Jitter_Exceeds_Threshold()
    {
        var jitterSamples = 8;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,

            // Leave OutlierMode at the default IqrFence so the auto-switch is eligible.
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                JitterCalibrationSamples = jitterSamples,
                JitterCalibrationWorkPerSample = 16,
                JitterAutoSwitchThreshold = 0.10,
            },
        };

        // First `jitterSamples` calls are the jitter probe: alternate between 500 and 1500 ns
        // -> mean 1000, stddev 500 -> CV 0.50, well above the 0.10 threshold. Subsequent calls
        // (Phase A calibration is skipped because OpsPerSample is pinned, warmup is 0, so the
        // next calls are the 3 measured samples) return a constant 1000 ns.
        var clock = new ScriptedClock(call => call < jitterSamples
            ? call % 2 == 0 ? 500.0 : 1500.0
            : 1000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.True(result.Diagnostic.OutlierDetectorSwitched);
        Assert.NotNull(result.Diagnostic.JitterMetric);

        Assert.True(result.Diagnostic.JitterMetric!.Value > 0.10,
            $"jitter metric {result.Diagnostic.JitterMetric} should exceed threshold 0.10");

        Assert.NotNull(result.EffectiveOutlierDetector);
        Assert.Equal("MAD (3×)", result.EffectiveOutlierDetector!.Name);

        // The switch produces a warning explaining what happened.
        Assert.Single(result.Warnings);
        Assert.Contains("auto-switch", result.Warnings[0]);
        Assert.Contains("IQR fence to Median Absolute Deviation", result.Warnings[0]);
    }

    [Fact]
    public void JitterCalibration_Does_Not_Switch_When_OutlierMode_Is_Not_Default_IqrFence()
    {
        var jitterSamples = 8;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,
            OutlierMode = OutlierMode.RemoveTop5Percent, // not IqrFence -> switch is not eligible
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                JitterCalibrationSamples = jitterSamples,
                JitterCalibrationWorkPerSample = 16,
                JitterAutoSwitchThreshold = 0.10,
            },
        };

        var clock = new ScriptedClock(call => call < jitterSamples
            ? call % 2 == 0 ? 500.0 : 1500.0
            : 1000.0);

        var result = RunSync(() => { }, options, clock);

        // The metric is still reported (the probe ran), but no switch because the user pinned
        // a non-default OutlierMode.
        Assert.NotNull(result.Diagnostic.JitterMetric);
        Assert.True(result.Diagnostic.JitterMetric!.Value > 0.10);
        Assert.False(result.Diagnostic.OutlierDetectorSwitched);
        Assert.Null(result.EffectiveOutlierDetector);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void JitterCalibration_Does_Not_Switch_When_Custom_OutlierDetector_Is_Set()
    {
        var jitterSamples = 8;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,

            // Custom detector pinned -> switch is not eligible, even with OutlierMode at default.
            OutlierDetector = OutlierDetectors.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                JitterCalibrationSamples = jitterSamples,
                JitterCalibrationWorkPerSample = 16,
                JitterAutoSwitchThreshold = 0.10,
            },
        };

        var clock = new ScriptedClock(call => call < jitterSamples
            ? call % 2 == 0 ? 500.0 : 1500.0
            : 1000.0);

        var result = RunSync(() => { }, options, clock);

        Assert.NotNull(result.Diagnostic.JitterMetric);
        Assert.True(result.Diagnostic.JitterMetric!.Value > 0.10);
        Assert.False(result.Diagnostic.OutlierDetectorSwitched);
        Assert.Null(result.EffectiveOutlierDetector);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void JitterCalibration_Disabled_AutoSwitch_With_NonPositive_Threshold_Still_Reports_Metric()
    {
        var jitterSamples = 8;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                JitterCalibrationSamples = jitterSamples,
                JitterCalibrationWorkPerSample = 16,
                JitterAutoSwitchThreshold = 0.0, // disable auto-switch, keep probe
            },
        };

        var clock = new ScriptedClock(call => call < jitterSamples
            ? call % 2 == 0 ? 500.0 : 1500.0
            : 1000.0);

        var result = RunSync(() => { }, options, clock);

        // The probe ran and the metric is high, but the auto-switch is disabled by the
        // non-positive threshold.
        Assert.NotNull(result.Diagnostic.JitterMetric);
        Assert.True(result.Diagnostic.JitterMetric!.Value > 0.10);
        Assert.False(result.Diagnostic.OutlierDetectorSwitched);
        Assert.Null(result.EffectiveOutlierDetector);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task JitterCalibration_AutoSwitches_In_Async_Path()
    {
        var jitterSamples = 8;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with
            {
                JitterCalibrationSamples = jitterSamples,
                JitterCalibrationWorkPerSample = 16,
                JitterAutoSwitchThreshold = 0.10,
            },
        };

        var clock = new ScriptedClock(call => call < jitterSamples
            ? call % 2 == 0 ? 500.0 : 1500.0
            : 1000.0);

        var spec = new RunSpec { Options = options };

        var result = await AdaptiveLoop.RunAsync(
            "bench",
            () => Task.CompletedTask,
            spec,
            clock,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            CancellationToken.None);

        Assert.True(result.Diagnostic.OutlierDetectorSwitched);
        Assert.NotNull(result.Diagnostic.JitterMetric);
        Assert.NotNull(result.EffectiveOutlierDetector);
        Assert.Single(result.Warnings);
    }

    private static AdaptiveResult RunSync(
        Action body,
        MeasurementOptions options,
        IClock clock,
        Action? setup = null,
        Action? teardown = null)
    {
        var spec = new RunSpec
        {
            Options = options,
            IterationSetup = setup,
            IterationTeardown = teardown,
        };

        return AdaptiveLoop.Run(
            "bench", body, spec, clock, NullBenchmarkProgress.Instance, NullMeasurementObserver.Instance, CancellationToken.None);
    }
}
