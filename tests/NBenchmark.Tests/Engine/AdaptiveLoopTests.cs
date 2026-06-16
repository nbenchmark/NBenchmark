using NBenchmark.Engine;
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
        Assert.Equal(3, result.ResolvedWarmup);
        Assert.Equal(2, result.Diagnostic.OpsPerSample);
        Assert.Equal(5, result.Diagnostic.ResolvedSamples);
        Assert.Equal(WarmupStopReason.ExplicitCount, result.Diagnostic.WarmupStop);
        Assert.Equal(SampleStopReason.ExplicitCount, result.Diagnostic.SampleStop);
        Assert.Equal((3 + 5) * 2L, result.Diagnostic.TotalBodyInvocations);
        Assert.Equal((3 + 5) * 2, bodyCalls);
    }

    [Fact]
    public void AutoWarmup_Discards_Prefix_From_Measured_Stats()
    {
        var bodyCalls = 0;
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,        // pin K so no calibration runs
            WarmupIterations = null, // auto warmup
            Iterations = 10,         // explicit measured count
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
        };

        // A flat signal settles the plateau rule at its floor.
        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => bodyCalls++, options, clock);

        // Constant signal: plateau settles at MinWarmup + PlateauPatience * BatchSize = 8 + 3 * 8 = 32.
        Assert.Equal(32, result.ResolvedWarmup);
        Assert.Equal(WarmupStopReason.Settled, result.Diagnostic.WarmupStop);

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
            Iterations = null,    // auto sample count -> CI detector
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

        var result = RunSync(() => bodyCalls++, options, clock, setup: () => setupCalls++);

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
    public void WallClock_Cap_Stops_A_Non_Converging_Measurement()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = null, // auto -> would otherwise collect at least MinSamples (30)
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { MaxTuningTime = TimeSpan.FromTicks(50) }, // 5000 ns
        };

        var clock = new ScriptedClock(1000.0);

        var result = RunSync(() => { }, options, clock);

        // Accumulated sample time crosses the 5000 ns cap on the 5th 1000 ns sample, far below MinSamples.
        Assert.Equal(SampleStopReason.WallClockCap, result.Diagnostic.SampleStop);
        Assert.Equal(5, result.PerOpTimings.Length);
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
            CancellationToken.None);

        Assert.Equal(4, result.PerOpTimings.Length);
        Assert.Equal(2, result.ResolvedWarmup);
        Assert.Equal(SampleStopReason.ExplicitCount, result.Diagnostic.SampleStop);
        Assert.Equal(2 + 4, bodyCalls); // warmup 2 + measured 4, at K = 1
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
            "bench", body, spec, clock, NullBenchmarkProgress.Instance, CancellationToken.None);
    }

    /// <summary>
    ///     A lenient, fully deterministic <see cref="IClock" /> for adaptive-loop tests: each timed
    ///     sample reads a scripted nanosecond value (by call index), and the clock never throws on
    ///     exhaustion. Elapsed-time queries return a tick count derived from the timestamp counter,
    ///     so measured/total durations stay monotonic without constraining the sample script.
    /// </summary>
    private sealed class ScriptedClock : IClock
    {
        private readonly Func<int, double> _sampleNs;
        private long _timestamp;
        private int _nsCall;

        public ScriptedClock(double constantNs) => _sampleNs = _ => constantNs;

        public ScriptedClock(Func<int, double> sampleNs) => _sampleNs = sampleNs;

        public long GetTimestamp() => ++_timestamp;

        public TimeSpan GetElapsedTime(long startTimestamp)
            => TimeSpan.FromTicks(Math.Max(0, _timestamp - startTimestamp));

        public double GetElapsedNanoseconds(long startTimestamp) => _sampleNs(_nsCall++);
    }
}
