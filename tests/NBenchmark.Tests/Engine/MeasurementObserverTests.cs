using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests.Engine;

public class MeasurementObserverTests
{
    [Fact]
    public void NullObserver_Produces_Same_Result_As_No_Observer()
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

        var clock = new ScriptedClock(2000.0);

        var nullResult = RunSync(() => bodyCalls++, options, clock, NullMeasurementObserver.Instance);
        var withObserver = RunSync(() => bodyCalls++, options, clock, new RecordingObserver());

        Assert.Equal(nullResult.PerOpTimings, withObserver.PerOpTimings);
        Assert.Equal(nullResult.Diagnostic, withObserver.Diagnostic);
    }

    [Fact]
    public void ExplicitCounts_Emit_Phase_Boundaries_And_Measurement_Samples()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 2,
            WarmupIterations = 3,
            Iterations = 5,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { EnableJitterCalibration = false },
        };

        // 2000 ns per sample at K = 2 -> 1000 ns per op.
        var clock = new ScriptedClock(2000.0);

        var observer = new RecordingObserver();
        var result = RunSync(() => bodyCalls++, options, clock, observer);

        // Jitter disabled: no Jitter phase events. Calibration skipped (OpsPerSample pinned).
        // WarmupIterations pinned -> Phase B is ExplicitCount, with a Starting event but no warmup
        // samples (RunUntimedSample does not emit OnSample because the timing path isn't taken).
        // Iterations pinned -> Phase C runs exactly 5 samples, CI detector is null, stop is ExplicitCount.
        Assert.DoesNotContain(observer.Phases, e => e.Phase == MeasurementPhase.Jitter);
        Assert.DoesNotContain(observer.Phases, e => e.Phase == MeasurementPhase.Calibration);

        Assert.Single(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Warmup && e.Transition == PhaseTransition.Starting
                                                    && e.WarmupStop is null);

        var warmupCompleted = Assert.Single(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Warmup && e.Transition == PhaseTransition.Completed);

        Assert.Equal(WarmupStopReason.ExplicitCount, warmupCompleted.WarmupStop);
        Assert.Equal(3, warmupCompleted.ResolvedWarmup);

        Assert.Single(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Measurement && e.Transition == PhaseTransition.Starting
                                                         && e.SampleStop is null);

        var measurementCompleted = Assert.Single(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Measurement && e.Transition == PhaseTransition.Completed);

        Assert.Equal(SampleStopReason.ExplicitCount, measurementCompleted.SampleStop);

        // No warmup samples (warmup is untimed), no calibration samples (OpsPerSample pinned).
        // Measurement emits every sample because ProgressCadence(5) = 1.
        Assert.DoesNotContain(observer.Samples, s => s.Warmup);
        Assert.Equal(5, observer.Samples.Count);
        Assert.All(observer.Samples, s => Assert.Equal(1000.0, s.PerOpNs));
        Assert.All(observer.Samples, s => Assert.Equal(2, s.K));

        Assert.Equal(5, result.Diagnostic.ResolvedSamples);
    }

    [Fact]
    public void AutoWarmup_Emits_Warmup_Samples_With_Warmup_Flag()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1, // pin K so no calibration runs
            WarmupIterations = null, // auto warmup
            Iterations = 10, // explicit measured count
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // Isolate the plateau rule from the warmup time floor and JIT gate so the scripted 1000 ns body
            // settles on the plateau (32) rather than running to MaxWarmup.
            AutoTune = AutoTuneOptions.Default with
            {
                EnableJitterCalibration = false,
                MinWarmupTime = TimeSpan.Zero,
                RequireJitQuiescence = false,
            },
        };

        // A flat signal settles the plateau rule at its floor.
        var clock = new ScriptedClock(1000.0);

        var observer = new RecordingObserver();
        var result = RunSync(() => bodyCalls++, options, clock, observer);

        // Constant signal: plateau settles at MinWarmup + PlateauPatience * BatchSize = 8 + 3 * 8 = 32.
        Assert.Equal(32, result.ResolvedWarmup);
        Assert.Equal(WarmupStopReason.Settled, result.Diagnostic.WarmupStop);

        // With Iterations=10, ProgressCadence(10) = 1, so every measured sample emits. The warmup
        // interval is ProgressCadence(MaxWarmup=10000) = 50, so warmup emits on samples 0, 50, 100...
        // - i.e. only sample 0 in a 32-sample warmup. Assert the Warmup flag distinguishes the two
        // phases, not the count.
        var warmupSamples = observer.Samples.Where(s => s.Warmup).ToList();
        var measuredSamples = observer.Samples.Where(s => !s.Warmup).ToList();
        Assert.NotEmpty(warmupSamples);
        Assert.NotEmpty(measuredSamples);
        Assert.All(warmupSamples, s => Assert.True(s.Warmup));
        Assert.All(measuredSamples, s => Assert.False(s.Warmup));

        var warmupCompleted = Assert.Single(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Warmup && e.Transition == PhaseTransition.Completed);

        Assert.Equal(WarmupStopReason.Settled, warmupCompleted.WarmupStop);
        Assert.Equal(32, warmupCompleted.ResolvedWarmup);
    }

    [Fact]
    public void AutoSamples_Emit_Measurement_Detector_Events_With_Live_Ci_Width()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0, // no warmup
            Iterations = null, // auto sample count -> CI detector
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // MinMeasurementTime = 0 isolates the CI stop rule from the measurement time floor, which
            // would otherwise hold this scripted 1 us body to its derived sample floor. This test is
            // about the detector events, not the floor.
            AutoTune = AutoTuneOptions.Default with
            {
                EnableJitterCalibration = false,
                MinMeasurementTime = TimeSpan.Zero,
            },
        };

        // Zero-variance signal -> CI half-width is 0, so the target is met at the first cadence point.
        var clock = new ScriptedClock(1000.0);

        var observer = new RecordingObserver();
        var result = RunSync(() => { }, options, clock, observer);

        // The first cadence multiple (BatchSize 8) at or past MinSamples (30) is 32.
        Assert.Equal(32, result.Diagnostic.ResolvedSamples);
        Assert.Equal(SampleStopReason.CiTargetMet, result.Diagnostic.SampleStop);

        // At least one measurement-phase detector event with the converged CI half-width.
        var measurementDetectors = observer.Detectors.Where(d => d.Phase == MeasurementPhase.Measurement).ToList();
        Assert.NotEmpty(measurementDetectors);

        var last = measurementDetectors[^1];
        Assert.Equal(32, last.SampleCount);
        Assert.Equal(1000.0, last.Mean);
        Assert.Equal(0.0, last.CiHalfWidth, 10);
        Assert.Equal(1, last.CurrentK);

        var measurementCompleted = Assert.Single(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Measurement && e.Transition == PhaseTransition.Completed);

        Assert.Equal(SampleStopReason.CiTargetMet, measurementCompleted.SampleStop);
    }

    [Fact]
    public void Calibration_Emits_Calibration_Phase_And_Samples()
    {
        var bodyCalls = 0;

        var options = MeasurementOptions.Default with
        {
            OpsPerSample = null, // auto-calibrate
            WarmupIterations = 0,
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            // Pin the 1 µs target this test's scripted 250/2000 ns timings assume (the default is now 10 µs).
            AutoTune = AutoTuneOptions.Default with { EnableJitterCalibration = false, TargetSampleDurationNs = 1_000 },
        };

        // K = 1 step (5 probes at 250 ns each -> fastest 250 < 1000 target, so K doubles).
        // K = 2 step (5 probes at 2000 ns each -> fastest 2000 >= 1000 target, so K settles at 2).
        // Measured samples at K = 2.
        var clock = new ScriptedClock(call => call switch
        {
            < 5 => 250.0,
            _ => 2000.0,
        });

        var observer = new RecordingObserver();
        var result = RunSync(() => bodyCalls++, options, clock, observer);

        Assert.Contains(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Calibration && e.Transition == PhaseTransition.Starting);

        var calibrationCompleted = Assert.Single(
            observer.Phases,
            e => e.Phase == MeasurementPhase.Calibration && e.Transition == PhaseTransition.Completed);

        Assert.Equal(2, calibrationCompleted.ResolvedK);

        // Calibration samples are flagged Warmup = true.
        var calibrationSamples = observer.Samples.Where(s => s.Warmup).ToList();
        Assert.NotEmpty(calibrationSamples);
        Assert.All(calibrationSamples, s => Assert.True(s.Warmup));

        Assert.Equal(2, result.Diagnostic.OpsPerSample);
    }

    [Fact]
    public void Jitter_Phase_Emits_Metric_And_Detector_Switch()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 3,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,

            // Default AutoTune has EnableJitterCalibration = true.
        };

        // Any finite timing - the jitter calibrator runs its own busy-weight loop internally, so
        // the body clock is only used for the calibration/warmup/measurement samples.
        var clock = new ScriptedClock(1000.0);

        var observer = new RecordingObserver();
        var result = RunSync(() => { }, options, clock, observer);

        var jitterPhases = observer.Phases.Where(e => e.Phase == MeasurementPhase.Jitter).ToList();
        Assert.Equal(2, jitterPhases.Count);

        var jitterStart = jitterPhases[0];
        Assert.Equal(PhaseTransition.Starting, jitterStart.Transition);
        Assert.Null(jitterStart.JitterMetric);

        var jitterCompleted = jitterPhases[1];
        Assert.Equal(PhaseTransition.Completed, jitterCompleted.Transition);
        Assert.NotNull(jitterCompleted.JitterMetric);

        _ = result;
    }

    [Fact]
    public void OnResult_Fires_With_Final_BenchmarkResult()
    {
        var options = MeasurementOptions.Default with
        {
            OpsPerSample = 1,
            WarmupIterations = 0,
            Iterations = 5,
            OutlierMode = OutlierMode.None,
            MeasureAllocationsOverride = false,
            AutoTune = AutoTuneOptions.Default with { EnableJitterCalibration = false },
        };

        var clock = new ScriptedClock(1000.0);

        var observer = new RecordingObserver();

        // Use the runner path (not AdaptiveLoop.Run directly) so OnResult fires - it's emitted by
        // BenchmarkRunner, not the loop. Inject the scripted clock via the internal runner ctor.
        var runner = new BenchmarkRunner(clock);
        var spec = new RunSpec { Options = options, Observer = observer };
        var outcome = runner.Run("bench", () => { }, spec);

        var captured = Assert.Single(observer.Results);
        Assert.Equal("bench", captured.Name);
        Assert.Equal(outcome.Result.Mean, captured.Mean);
        Assert.Equal(5, captured.N);
    }

    private static AdaptiveResult RunSync(
        Action body,
        MeasurementOptions options,
        IClock clock,
        IMeasurementObserver observer)
    {
        var spec = new RunSpec
        {
            Options = options,
        };

        return AdaptiveLoop.Run(
            "bench", body, spec, clock, NullBenchmarkProgress.Instance, observer, CancellationToken.None);
    }

    private sealed class RecordingObserver : IMeasurementObserver
    {
        public List<MeasurementPhaseEvent> Phases { get; } = [];
        public List<SampleEvent> Samples { get; } = [];
        public List<DetectorStateEvent> Detectors { get; } = [];
        public List<BenchmarkResult> Results { get; } = [];

        public void OnPhase(in MeasurementPhaseEvent e) => Phases.Add(e);
        public void OnSample(in SampleEvent e) => Samples.Add(e);
        public void OnDetector(in DetectorStateEvent e) => Detectors.Add(e);
        public void OnResult(BenchmarkResult result) => Results.Add(result);
    }
}
