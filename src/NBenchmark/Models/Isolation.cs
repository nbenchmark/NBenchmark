namespace NBenchmark;

/// <summary>
///     Whether a measurement runs in a dedicated worker process, and what happens when it cannot.
/// </summary>
/// <remarks>
///     <para>
///         Isolation is the library's central claim, so it gets one vocabulary. This enum is the whole
///         of it: <see cref="MeasurementOptions.Isolation" /> holds it, <c>WithIsolation</c> sets it on
///         every builder, and <c>[Isolation]</c> declares it per benchmark or per class. The
///         command-line spellings map onto it too - <c>--in-process</c> is <see cref="Off" /> and
///         <c>--strict-isolation</c> is <see cref="Required" />.
///     </para>
///     <para>
///         The distinction between <see cref="Required" /> and <see cref="Preferred" /> is about a
///         <i>refusal</i> - a benchmark NBenchmark declines to isolate because its shape cannot cross a
///         process boundary - and never about <see cref="Off" />, which is the caller getting exactly
///         what they asked for. See <see cref="IsolationStatus" /> for how the outcome is reported.
///     </para>
/// </remarks>
public enum Isolation
{
    /// <summary>
    ///     Measure in a worker process, and fail the run when that is refused. The default, because
    ///     the in-process fallback should be something a user asks for, never something that happens
    ///     to them.
    /// </summary>
    Required = 0,

    /// <summary>
    ///     Measure in a worker process when possible; when isolation is refused, measure in the host
    ///     process and label the result with the reason rather than failing.
    /// </summary>
    Preferred = 1,

    /// <summary>
    ///     Measure in the host process. Reach for this when the current process <i>is</i> the subject:
    ///     cold-start cost, or a body that must observe host state a fresh process cannot rebuild.
    ///     Results are stamped <see cref="IsolationStatus.InProcessRequested" />, so they are never
    ///     silently compared against an isolated measurement.
    /// </summary>
    Off = 2,
}
