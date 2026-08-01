using System.Collections.Immutable;
using System.Diagnostics;

namespace NBenchmark.Engine;

/// <summary>
///     An <see cref="IMeasurementObserver" /> that fans every event out to a list of child
///     observers. Used when a harness or suite has multiple observers attached (for example a
///     live dashboard plus a file-logging observer): the loop sees one composite, the composite
///     dispatches to each child.
/// </summary>
/// <remarks>
///     <para>
///         The hot-path guard in <c>AdaptiveLoop</c> is a single reference comparison
///         (<c>observer != NullMeasurementObserver.Instance</c>). A composite with at least one
///         child is not the null singleton, so the guard passes once and the composite fans out.
///         A composite with no children must never be constructed - the harness and suite resolve
///         an empty observer list back to <see cref="NullMeasurementObserver.Instance" /> so the
///         hot path stays observation-free.
///     </para>
///     <para>
///         Each child dispatch is wrapped in a try/catch so one throwing observer cannot kill the
///         stream for the others. The contract on <see cref="IMeasurementObserver" /> is "must not
///         throw"; this defence-in-depth isolates a misbehaving observer rather than propagating.
///         Exceptions are traced so a host with a <see cref="TraceListener" /> attached can see why
///         an observer stopped receiving events. The trace matches the silent-failure convention
///         used by <c>ReporterRegistry</c> / <c>ObserverRegistry</c> extension-load failures: no
///         console output, so benchmark results are not polluted.
///     </para>
/// </remarks>
public sealed class CompositeMeasurementObserver : IMeasurementObserver
{
    private readonly ImmutableArray<IMeasurementObserver> _observers;

    /// <summary>The child observers this composite fans out to.</summary>
    public IReadOnlyList<IMeasurementObserver> Observers => _observers;

    /// <summary>
    ///     <c>true</c> when any child observer wants the live per-sample stream forwarded across a
    ///     worker boundary. The worker-group runner turns <see cref="MeasurementOptions.StreamSamples" />
    ///     on when this is <c>true</c>, so a consumer that needs the stream gets it without the
    ///     caller having to set the flag. Aggregated as a disjunction so a single sample-stream
    ///     consumer attached alongside phase-only observers still gets the stream.
    /// </summary>
    public bool WantsSampleStream { get; }

    /// <summary>
    ///     Creates a composite over the supplied observers. Callers MUST filter out
    ///     <see cref="NullMeasurementObserver.Instance" /> before constructing the composite so a
    ///     single null-observer attachment does not pay a dispatch into a no-op. The
    ///     <see cref="BenchmarkHarness" /> and <see cref="BenchmarkSuite" /> do this when they
    ///     resolve the attached list.
    /// </summary>
    public CompositeMeasurementObserver(IEnumerable<IMeasurementObserver> observers)
    {
        ArgumentNullException.ThrowIfNull(observers);
        _observers = [.. observers];
        Debug.Assert(_observers.Length > 0, "CompositeMeasurementObserver must not be constructed with zero children.");
        Debug.Assert(!_observers.Any(o => o is NullMeasurementObserver), "CompositeMeasurementObserver must not contain NullMeasurementObserver.Instance.");

        WantsSampleStream = _observers.Any(o => o.WantsSampleStream);
    }

    public void OnPhase(in MeasurementPhaseEvent e)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnPhase(in e);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: observer '{0}' threw from OnPhase and was skipped: {1}",
                    observer.GetType().FullName, ex.Message);
            }
        }
    }

    public void OnSample(in SampleEvent e)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnSample(in e);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: observer '{0}' threw from OnSample and was skipped: {1}",
                    observer.GetType().FullName, ex.Message);
            }
        }
    }

    public void OnDetector(in DetectorStateEvent e)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnDetector(in e);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: observer '{0}' threw from OnDetector and was skipped: {1}",
                    observer.GetType().FullName, ex.Message);
            }
        }
    }

    public void OnResult(BenchmarkResult result)
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.OnResult(result);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: observer '{0}' threw from OnResult and was skipped: {1}",
                    observer.GetType().FullName, ex.Message);
            }
        }
    }

    /// <summary>
    ///     Fans <c>Dispose</c> out to each child in a try/catch (mirroring the per-dispatch
    ///     isolation of <see cref="OnPhase" /> / <see cref="OnSample" /> /
    ///     <see cref="OnDetector" /> / <see cref="OnResult" />), so a throwing
    ///     <c>Dispose</c> from one observer cannot prevent the others from disposing. Called by
    ///     the harness/suite <c>using</c> on the resolved observer at the end of
    ///     <c>RunAsync</c>; the composite is the resolution shape whenever two or more observers
    ///     are attached (auto-attached + explicit, or multiple programmatic), so every child's
    ///     <c>Dispose</c> runs on both the success and exception paths.
    /// </summary>
    public void Dispose()
    {
        foreach (var observer in _observers)
        {
            try
            {
                observer.Dispose();
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    "NBenchmark: observer '{0}' threw from Dispose and was skipped: {1}",
                    observer.GetType().FullName, ex.Message);
            }
        }
    }
}
