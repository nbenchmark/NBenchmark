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
///     <para>
///         The interface extends <see cref="IDisposable" /> via a default no-op
///         <c>Dispose</c> member (a default interface member, C# 8 / .NET Core 3.0+). Implementations
///         that own long-lived resources (an <c>HttpClient</c>, a pump task, a channel writer)
///         override <c>Dispose</c> to tear them down; implementations with no resources inherit
///         the no-op and require no code change. The harness and suite wrap the resolved
///         observer in a <c>using</c> so the dispose runs on both the success and exception
///         paths at the end of <c>RunAsync</c>.
///     </para>
/// </remarks>
public interface IMeasurementObserver : IDisposable
{
    /// <summary>
    ///     The registry name this observer was constructed under, or <c>null</c> for a
    ///     programmatically attached instance not registered through
    ///     <see cref="Observers.ObserverRegistry" />. Used by <c>BenchmarkHarness.ResolveObserver</c>
    ///     and <c>BenchmarkSuite.ResolveObserver</c> to dedup an auto-attached observer against a
    ///     programmatic <c>.WithObserver(...)</c> instance of the same name, mirroring
    ///     <see cref="Reporters.IReporter.Name" /> dedup in
    ///     <see cref="Reporters.ReporterRegistry.InvokeReportersAsync" />. Default is
    ///     <c>null</c> so existing implementations that do not declare a name continue to
    ///     compile and run unchanged.
    /// </summary>
    public string? Name => null;

    /// <summary>
    ///     Disposes resources held by this observer. The default implementation is a no-op so
    ///     implementations with no unmanaged resources inherit it without code change. The
    ///     harness and suite wrap the resolved observer in a <c>using</c>, so this runs on both
    ///     the success and exception paths at the end of <c>RunAsync</c>. Implementations that
    ///     override this MUST guard against double-dispose (the <c>using</c> may run after an
    ///     explicit <c>Dispose</c> call from a test or a user).
    /// </summary>
    void IDisposable.Dispose()
    {
    }

    /// <summary>
    ///     Reports a phase transition (jitter, calibration, warmup, measurement, suite-completed)
    ///     - starting or completed - with the phase's resolved outcome where applicable
    ///     (jitter metric, resolved K, warmup stop reason, sample stop reason, success flag).
    /// </summary>
    public void OnPhase(in MeasurementPhaseEvent e);

    /// <summary>
    ///     Reports one timed sample. Called between samples, after the per-op nanoseconds are
    ///     computed and before the next body invocation - outside the timed region. The
    ///     <see cref="SampleEvent.Warmup" /> flag distinguishes calibration/warmup samples from
    ///     measured samples so a consumer can plot the warmup-settling curve live.
    /// </summary>
    public void OnSample(in SampleEvent e);

    /// <summary>
    ///     Reports a snapshot of the running detector state: running mean, sample standard
    ///     deviation, confidence-interval half-width, sample count, and current ops-per-sample
    ///     (<c>K</c>). Emitted after a detector update so a consumer can plot the autotune
    ///     convergence curve live.
    /// </summary>
    public void OnDetector(in DetectorStateEvent e);

    /// <summary>
    ///     Reports the post-trim summary result for one benchmark, mirroring
    ///     <see cref="IBenchmarkProgress.OnBenchmarkCompleted" />. Fires after the runner builds
    ///     the <see cref="BenchmarkResult" /> - for success, dry-run, and errored outcomes - so a
    ///     consumer sees every benchmark in the run.
    /// </summary>
    public void OnResult(BenchmarkResult result);
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
