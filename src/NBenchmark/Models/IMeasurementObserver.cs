namespace NBenchmark;

/// <summary>
///     A non-perturbing observation channel for live measurement telemetry. Implementations
///     MUST return immediately and MUST NOT block, allocate on the hot path, or do I/O - the
///     measurement loop calls these between samples on the timing-critical thread. Hand any
///     real work to another thread (a bounded channel, a queue, a task) and return at once.
/// </summary>
/// <remarks>
///     This is a parallel seam to <see cref="IBenchmarkProgress" />: progress carries
///     lifecycle/percent-complete signals with no measurement payload, while the observer
///     carries the per-sample stream, the live detector state, and phase transitions. The
///     two are invoked from the same emit points in <c>AdaptiveLoop</c> but address different
///     consumers. <see cref="NullMeasurementObserver" /> is the default; the loop pays one
///     null check per sample when no observer is attached.
/// </remarks>
public interface IMeasurementObserver
{
    /// <summary>
    ///     Reports a phase transition (jitter, calibration, warmup, measurement) - starting or
    ///     completed - with the phase's resolved outcome where applicable (jitter metric, resolved
    ///     K, warmup stop reason, sample stop reason).
    /// </summary>
    void OnPhase(in MeasurementPhaseEvent e);

    /// <summary>
    ///     Reports one timed sample. Called between samples, after the per-op nanoseconds are
    ///     computed and before the next body invocation - outside the timed region. The
    ///     <see cref="SampleEvent.Warmup" /> flag distinguishes calibration/warmup samples from
    ///     measured samples so a consumer can plot the warmup-settling curve live.
    /// </summary>
    void OnSample(in SampleEvent e);

    /// <summary>
    ///     Reports a snapshot of the running detector state: running mean, sample standard
    ///     deviation, confidence-interval half-width, sample count, and current ops-per-sample
    ///     (<c>K</c>). Emitted after a detector update so a consumer can plot the autotune
    ///     convergence curve live.
    /// </summary>
    void OnDetector(in DetectorStateEvent e);

    /// <summary>
    ///     Reports the post-trim summary result for one benchmark, mirroring
    ///     <see cref="IBenchmarkProgress.OnBenchmarkCompleted" />. Fires after the runner builds
    ///     the <see cref="BenchmarkResult" /> - for success, dry-run, and errored outcomes - so a
    ///     consumer sees every benchmark in the run.
    /// </summary>
    void OnResult(BenchmarkResult result);
}

/// <summary>
///     The default no-op observer. The loop's hot-path branch is
///     <c>observer != NullMeasurementObserver.Instance</c>, so attaching this singleton is
///     observation-free. Mirrors <see cref="NullBenchmarkProgress" />.
/// </summary>
public sealed class NullMeasurementObserver : IMeasurementObserver
{
    public static readonly NullMeasurementObserver Instance = new();

    private NullMeasurementObserver()
    {
    }

    public void OnPhase(in MeasurementPhaseEvent e)
    {
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
}