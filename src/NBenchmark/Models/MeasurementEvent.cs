namespace NBenchmark;

/// <summary>
///     A tagged union of the four live events an <see cref="IMeasurementObserver" /> can receive:
///     a phase boundary, a timed sample, a detector-state snapshot, or a final result. Exactly one
///     of <see cref="PhaseEvent" />, <see cref="SampleEvent" />, <see cref="DetectorStateEvent" /> or
///     <see cref="Result" /> is populated, selected by <see cref="Kind" />.
/// </summary>
public readonly record struct MeasurementEvent
{
    /// <summary>Which payload of a <see cref="MeasurementEvent" /> is populated.</summary>
    public enum EventKind
    {
        /// <summary><see cref="PhaseEvent" /> is populated.</summary>
        Phase,

        /// <summary><see cref="SampleEvent" /> is populated.</summary>
        Sample,

        /// <summary><see cref="DetectorStateEvent" /> is populated.</summary>
        DetectorState,

        /// <summary><see cref="Result" /> is populated.</summary>
        Result,
    }

    private MeasurementEvent(EventKind kind, MeasurementPhaseEvent phase, SampleEvent sample, DetectorStateEvent detector, BenchmarkResult? result)
    {
        Kind = kind;
        PhaseEvent = phase;
        SampleEvent = sample;
        DetectorStateEvent = detector;
        Result = result;
    }

    /// <summary>Wraps a phase-boundary event.</summary>
    public MeasurementEvent(MeasurementPhaseEvent e)
        : this(EventKind.Phase, e, default, default, null)
    {
    }

    /// <summary>Wraps a timed-sample event.</summary>
    public MeasurementEvent(SampleEvent e)
        : this(EventKind.Sample, default, e, default, null)
    {
    }

    /// <summary>Wraps a detector-state snapshot event.</summary>
    public MeasurementEvent(DetectorStateEvent e)
        : this(EventKind.DetectorState, default, default, e, null)
    {
    }

    /// <summary>Wraps a completed benchmark result.</summary>
    public MeasurementEvent(BenchmarkResult result)
        : this(EventKind.Result, default, default, default, result)
    {
    }

    /// <summary>Which of the four payloads this event carries.</summary>
    public EventKind Kind { get; }

    /// <summary>The phase-boundary payload, populated when <see cref="Kind" /> is <see cref="EventKind.Phase" />.</summary>
    public MeasurementPhaseEvent PhaseEvent { get; }

    /// <summary>The timed-sample payload, populated when <see cref="Kind" /> is <see cref="EventKind.Sample" />.</summary>
    public SampleEvent SampleEvent { get; }

    /// <summary>The detector-state payload, populated when <see cref="Kind" /> is <see cref="EventKind.DetectorState" />.</summary>
    public DetectorStateEvent DetectorStateEvent { get; }

    /// <summary>The completed result, populated when <see cref="Kind" /> is <see cref="EventKind.Result" />.</summary>
    public BenchmarkResult? Result { get; }
}
