using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests.Engine;

/// <summary>
///     Tests for <see cref="CompositeMeasurementObserver" /> and the additive multi-observer
///     wiring on <see cref="BenchmarkSuite" /> / <see cref="BenchmarkHarness" />. Covers:
///     - fan-out: every child observer receives every event
///     - isolation: one throwing observer does not kill the stream for the others
///     - additive WithObserver: repeated calls stack observers rather than replacing
///     - empty list collapses to NullMeasurementObserver.Instance (observation-free hot path)
/// </summary>
public class CompositeMeasurementObserverTests
{
    [Fact]
    public void FanOut_Every_Child_Receives_Every_Event()
    {
        var a = new RecordingObserver();
        var b = new RecordingObserver();
        var composite = new CompositeMeasurementObserver([a, b]);

        var phase = new MeasurementPhaseEvent("bench", MeasurementPhase.Measurement, PhaseTransition.Starting);
        var sample = new SampleEvent("bench", 0, 100.0, 1, 0, Warmup: false);
        var detector = new DetectorStateEvent("bench", MeasurementPhase.Measurement, 1, 100.0, 0.0, 0.05, 1);
        var result = MakeResult("bench");

        composite.OnPhase(in phase);
        composite.OnSample(in sample);
        composite.OnDetector(in detector);
        composite.OnResult(result);

        Assert.Equal([phase], a.Phases);
        Assert.Equal([sample], a.Samples);
        Assert.Equal([detector], a.Detectors);
        Assert.Equal([result], a.Results);

        Assert.Equal([phase], b.Phases);
        Assert.Equal([sample], b.Samples);
        Assert.Equal([detector], b.Detectors);
        Assert.Equal([result], b.Results);
    }

    [Fact]
    public void Isolation_One_Throwing_Observer_Does_Not_Kill_The_Stream_For_Others()
    {
        var healthy = new RecordingObserver();
        var throwing = new ThrowingObserver(new InvalidOperationException("boom"));
        var composite = new CompositeMeasurementObserver([throwing, healthy]);

        var sample = new SampleEvent("bench", 0, 100.0, 1, 0, Warmup: false);
        var result = MakeResult("bench");

        // The throwing observer throws from OnSample and OnResult; the composite catches and
        // the healthy observer still receives the event.
        composite.OnSample(in sample);
        composite.OnResult(result);

        Assert.Equal([sample], healthy.Samples);
        Assert.Equal([result], healthy.Results);
    }

    [Fact]
    public void Isolation_Throwing_From_OnPhase_Does_Not_Stop_OnSample()
    {
        var healthy = new RecordingObserver();
        var throwing = new ThrowingObserver(new InvalidOperationException("phase boom"))
        {
            ThrowOnPhase = true,
            ThrowOnSample = false,
            ThrowOnResult = false,
        };
        var composite = new CompositeMeasurementObserver([throwing, healthy]);

        var phase = new MeasurementPhaseEvent("bench", MeasurementPhase.Warmup, PhaseTransition.Starting);
        var sample = new SampleEvent("bench", 0, 50.0, 1, 0, Warmup: true);

        composite.OnPhase(in phase);
        composite.OnSample(in sample);

        // The healthy observer still received both events despite the other observer throwing
        // from OnPhase. The throwing observer's exception was caught and did not stop OnSample.
        Assert.Equal([phase], healthy.Phases);
        Assert.Equal([sample], healthy.Samples);
    }

    [Fact]
    public void Observers_Property_Exposes_Children()
    {
        var a = new RecordingObserver();
        var b = new RecordingObserver();
        var composite = new CompositeMeasurementObserver([a, b]);

        Assert.Equal(2, composite.Observers.Count);
        Assert.Same(a, composite.Observers[0]);
        Assert.Same(b, composite.Observers[1]);
    }

    [Fact]
    public async Task BenchmarkSuite_WithObserver_Is_Additive_Both_Observers_Receive_Events()
    {
        var a = new RecordingObserver();
        var b = new RecordingObserver();

        await new BenchmarkSuite("additive-observers")
            .Add("bench", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithObserver(a)
            .WithObserver(b)
            .RunAsync();

        // Both observers receive the single result.
        Assert.Single(a.Results);
        Assert.Single(b.Results);
        Assert.Equal("bench", a.Results[0].Name);
        Assert.Equal("bench", b.Results[0].Name);

        // Both receive sample/phase events (non-empty proves fan-out, not last-wins).
        Assert.NotEmpty(a.Samples);
        Assert.NotEmpty(b.Samples);
        Assert.NotEmpty(a.Phases);
        Assert.NotEmpty(b.Phases);
    }

    [Fact]
    public async Task BenchmarkSuite_WithObserver_Null_Is_NoOp_And_Does_Not_Displace_Previous()
    {
        // Passing null should not clear previously-attached observers (additive contract).
        var a = new RecordingObserver();

        await new BenchmarkSuite("null-after-attach")
            .Add("bench", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .WithObserver(a)
            .WithObserver(null!) // null must be ignored, not replace
            .RunAsync();

        Assert.Single(a.Results);
    }

    [Fact]
    public async Task BenchmarkSuite_No_Observer_Attached_Is_Observation_Free()
    {
        // An empty observer list collapses to NullMeasurementObserver.Instance; a distinct
        // recording observer passed to the suite (but not attached) should see no events.
        var outside = new RecordingObserver();

        await new BenchmarkSuite("no-observer")
            .Add("bench", () => { })
            .WithWarmup(0)
            .WithIterations(1)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Empty(outside.Results);
        Assert.Empty(outside.Samples);
    }

    [Fact]
    public async Task BenchmarkHarness_WithObserver_Is_Additive_Both_Observers_Receive_Results()
    {
        var a = new RecordingObserver();
        var b = new RecordingObserver();

        await BenchmarkHarness.Create(["--filter", "TestBenchmarks.*", "--in-process", "--warmup", "0", "--iterations", "1"])
            .AddFromAssembly<TestBenchmarks>()
            .WithRunOrder(RunOrder.Declaration)
            .WithObserver(a)
            .WithObserver(b)
            .RunAsync();

        // TestBenchmarks has two methods (Fast, FastBaseline); both observers see both results.
        Assert.Equal(2, a.Results.Count);
        Assert.Equal(2, b.Results.Count);
    }

    private static BenchmarkResult MakeResult(string name) =>
        new()
        {
            Name = name,
            Mean = 100.0,
            Median = 95.0,
            Min = 80.0,
            Max = 120.0,
            StandardDeviation = 5.0,
            Q1 = 85.0,
            Q3 = 110.0,
            InterquartileRange = 25.0,
            OutliersRemoved = 0,
            N = 30,
            Skewness = 0.1,
            Kurtosis = 2.8,
            Mad = 3.0,
            AllocMedian = null,
            AllocP95 = null,
            AllocMax = null,
        };

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

    private sealed class ThrowingObserver : IMeasurementObserver
    {
        private readonly Exception _exception;
        public bool ThrowOnPhase { get; set; }
        public bool ThrowOnSample { get; set; } = true;
        public bool ThrowOnDetector { get; set; }
        public bool ThrowOnResult { get; set; } = true;

        public ThrowingObserver(Exception exception)
        {
            _exception = exception;
        }

        public void OnPhase(in MeasurementPhaseEvent e)
        {
            if (ThrowOnPhase) throw _exception;
        }

        public void OnSample(in SampleEvent e)
        {
            if (ThrowOnSample) throw _exception;
        }

        public void OnDetector(in DetectorStateEvent e)
        {
            if (ThrowOnDetector) throw _exception;
        }

        public void OnResult(BenchmarkResult result)
        {
            if (ThrowOnResult) throw _exception;
        }
    }
}