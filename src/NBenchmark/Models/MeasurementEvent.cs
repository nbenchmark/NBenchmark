namespace NBenchmark;

public readonly record struct MeasurementEvent
{
    public enum EventKind
    {
        Phase,
        Sample,
        DetectorState,
        Result,
    }

    private MeasurementEvent(EventKind kind, MeasurementPhaseEvent phase, SampleEvent sample, DetectorStateEvent detector, BenchmarkResult? result)
    {
        Kind = kind;
        PhaseEvent = phase;
        SampleEvent = sample;
        DetectorStateEvent = detector;
        _result = result;
    }

    public MeasurementEvent(MeasurementPhaseEvent e)
        : this(EventKind.Phase, e, default, default, null)
    {
    }

    public MeasurementEvent(SampleEvent e)
        : this(EventKind.Sample, default, e, default, null)
    {
    }

    public MeasurementEvent(DetectorStateEvent e)
        : this(EventKind.DetectorState, default, default, e, null)
    {
    }

    public MeasurementEvent(BenchmarkResult result)
        : this(EventKind.Result, default, default, default, result)
    {
    }

    public EventKind Kind { get; }

    public MeasurementPhaseEvent PhaseEvent { get; }

    public SampleEvent SampleEvent { get; }

    public DetectorStateEvent DetectorStateEvent { get; }

    private readonly BenchmarkResult? _result;

    public BenchmarkResult? Result => _result;
}
