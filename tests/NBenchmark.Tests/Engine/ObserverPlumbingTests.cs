using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests.Engine;

/// <summary>
///     Plumbing tests that verify the observer is threaded correctly through the stack:
///     WithObserver on BenchmarkSuite/BenchmarkHarness → RunSpec.Observer → AdaptiveLoop → events.
///     These tests use a minimal body (no-op, no calibration) to focus on wiring, not measurement.
/// </summary>
public class ObserverPlumbingTests
{
    [Fact]
    public async Task BenchmarkSuite_WithObserver_Forwarded_And_OnResult_Fires()
    {
        var observer = new RecordingObserver();

        await new BenchmarkSuite("observer-plumbing").WithIsolation(Isolation.Preferred)
            .Add("fast", () => { })
            .Add("slow", () => { })
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .WithObserver(observer)
            .RunAsync();

        // OnResult fires exactly once per benchmark (2 benchmarks → 2 results).
        Assert.Equal(2, observer.Results.Count);
        Assert.Contains(observer.Results, r => r.Name == "fast");
        Assert.Contains(observer.Results, r => r.Name == "slow");

        // Phase events emitted for each benchmark (Jitter disabled in default options with
        // EnableJitterCalibration=false on AutoTuneOptions.Default when OpsPerSample is pinned).
        Assert.NotEmpty(observer.Phases);
        Assert.NotEmpty(observer.Samples);
    }

    [Fact]
    public async Task BenchmarkSuite_Null_Observer_Receives_No_Events()
    {
        var observer = new RecordingObserver();

        await new BenchmarkSuite("null-observer").WithIsolation(Isolation.Preferred)
            .Add("bench", () => { })
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithObserver(NullMeasurementObserver.Instance) // explicit null - same as default
            .RunAsync();

        Assert.Empty(observer.Results);
        Assert.Empty(observer.Samples);
        Assert.Empty(observer.Phases);
    }

    [Fact]
    public async Task BenchmarkSuite_Observer_Events_Carry_Correct_Benchmark_Names()
    {
        var observer = new RecordingObserver();

        await new BenchmarkSuite("name-check").WithIsolation(Isolation.Preferred)
            .Add("alpha", () => { })
            .Add("beta", () => { })
            .WithWarmupSamples(0)
            .WithSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithRunOrder(RunOrder.Declaration)
            .WithObserver(observer)
            .RunAsync();

        // All sample events should carry the benchmark name of the benchmark being measured.
        var alphaSamples = observer.Samples.Where(s => s.BenchmarkName == "alpha").ToList();
        var betaSamples = observer.Samples.Where(s => s.BenchmarkName == "beta").ToList();

        Assert.NotEmpty(alphaSamples);
        Assert.NotEmpty(betaSamples);

        // All phase events for alpha should carry the alpha name.
        var alphaPhases = observer.Phases.Where(p => p.BenchmarkName == "alpha").ToList();
        var betaPhases = observer.Phases.Where(p => p.BenchmarkName == "beta").ToList();

        Assert.NotEmpty(alphaPhases);
        Assert.NotEmpty(betaPhases);
    }

    [Fact]
    public void BenchmarkRunner_WithObserver_Forwarded_Via_RunSpec()
    {
        var observer = new RecordingObserver();
        var clock = new ScriptedClock(1000.0);
        var runner = new BenchmarkRunner(clock);

        var spec = new RunSpec
        {
            Options = new MeasurementOptions
            {
                WarmupSamples = 0,
                Samples = 2,
                OutlierMode = OutlierMode.None,
                MeasureAllocations = false,
            },
            Observer = observer,
        };

        var result = runner.Run("bench", () => { }, spec);

        // OnResult fires for the result.
        Assert.Single(observer.Results);
        Assert.Equal("bench", observer.Results[0].Name);

        // Phase events for the measurement phase (no warmup, no calibration, no jitter).
        var measurementPhases = observer.Phases.Where(p => p.Phase == MeasurementPhase.Measurement).ToList();
        Assert.Equal(2, measurementPhases.Count); // Starting + Completed

        Assert.Equal(PhaseTransition.Starting, measurementPhases[0].Transition);
        Assert.Equal(PhaseTransition.Completed, measurementPhases[1].Transition);
        Assert.Equal(SampleStopReason.ExplicitCount, measurementPhases[1].SampleStop);

        // Measured samples have Warmup=false, calibration samples have Warmup=true.
        // Filter to measured samples only (calibration is non-empty when OpsPerSample is null).
        var measuredSamples = observer.Samples.Where(s => !s.Warmup).ToList();
        Assert.Equal(2, measuredSamples.Count);
        Assert.All(measuredSamples, s => Assert.False(s.Warmup));
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
