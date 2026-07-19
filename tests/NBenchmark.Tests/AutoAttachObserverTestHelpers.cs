namespace NBenchmark.Tests;

internal sealed class CapturingAutoObserver : IMeasurementObserver
{
    public CapturingAutoObserver(string name)
    {
        Name = name;
    }

    public List<MeasurementPhaseEvent> Phases { get; } = [];
    public List<SampleEvent> Samples { get; } = [];
    public List<DetectorStateEvent> Detectors { get; } = [];
    public List<BenchmarkResult> Results { get; } = [];
    public int DisposeCallCount { get; private set; }
    public bool IsDisposed => DisposeCallCount > 0;

    public string Name { get; }

    public void OnPhase(in MeasurementPhaseEvent e) => Phases.Add(e);
    public void OnSample(in SampleEvent e) => Samples.Add(e);
    public void OnDetector(in DetectorStateEvent e) => Detectors.Add(e);
    public void OnResult(BenchmarkResult result) => Results.Add(result);

    public void Dispose() => DisposeCallCount++;
}

internal sealed class CountingAutoObserver(string name) : IMeasurementObserver
{
    public int DisposeCallCount { get; private set; }

    public List<MeasurementPhaseEvent> Phases { get; } = [];
    public List<SampleEvent> Samples { get; } = [];
    public List<BenchmarkResult> Results { get; } = [];
    public string Name => name;

    public void OnPhase(in MeasurementPhaseEvent e) => Phases.Add(e);
    public void OnSample(in SampleEvent e) => Samples.Add(e);

    public void OnDetector(in DetectorStateEvent e)
    {
    }

    public void OnResult(BenchmarkResult result) => Results.Add(result);

    public void Dispose() => DisposeCallCount++;
}

internal sealed class OrderTrackingObserver(string name, List<string> order) : IMeasurementObserver
{
    public string Name => name;

    public void OnPhase(in MeasurementPhaseEvent e)
    {
        // Only track the SuiteCompleted sentinel so the order assertion counts one
        // dispatch per observer per run, not one per per-benchmark phase event.
        if (e.Phase == MeasurementPhase.SuiteCompleted)
            order.Add(name);
    }

    public void OnSample(in SampleEvent e)
    {
    }

    public void OnDetector(in DetectorStateEvent e)
    {
    }

    public void OnResult(BenchmarkResult result)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class ThrowingAutoObserver : IMeasurementObserver
{
    private readonly Exception _exception;

    public ThrowingAutoObserver(string name, Exception exception)
    {
        Name = name;
        _exception = exception;
    }

    public bool ThrowOnPhase { get; set; }
    public bool ThrowOnSample { get; set; } = true;
    public bool ThrowOnDetector { get; set; }
    public bool ThrowOnResult { get; set; } = true;
    public bool ThrowOnDispose { get; set; }
    public int DisposeCallCount { get; private set; }

    public string Name { get; }

    public void OnPhase(in MeasurementPhaseEvent e)
    {
        if (ThrowOnPhase)
            throw _exception;
    }

    public void OnSample(in SampleEvent e)
    {
        if (ThrowOnSample)
            throw _exception;
    }

    public void OnDetector(in DetectorStateEvent e)
    {
        if (ThrowOnDetector)
            throw _exception;
    }

    public void OnResult(BenchmarkResult result)
    {
        if (ThrowOnResult)
            throw _exception;
    }

    public void Dispose()
    {
        DisposeCallCount++;

        if (ThrowOnDispose)
            throw _exception;
    }
}

internal sealed class AnonymousObserver : IMeasurementObserver
{
    public List<SampleEvent> Samples { get; } = [];
    public List<BenchmarkResult> Results { get; } = [];

    public void OnPhase(in MeasurementPhaseEvent e)
    {
    }

    public void OnSample(in SampleEvent e) => Samples.Add(e);

    public void OnDetector(in DetectorStateEvent e)
    {
    }

    public void OnResult(BenchmarkResult result) => Results.Add(result);

    public void Dispose()
    {
    }
}
