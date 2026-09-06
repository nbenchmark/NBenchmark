namespace NBenchmark;

/// <summary>
///     The phases of the adaptive measurement loop, reported on
///     <see cref="MeasurementPhaseEvent.Phase" /> and <see cref="SampleEvent" /> (via the
///     <see cref="SampleEvent.Warmup" /> flag). Each benchmark runs through them in order:
///     jitter, calibration, warmup, measurement. A pinned configuration short-circuits a
///     phase but still reports its boundary. <see cref="SuiteCompleted" /> is emitted once
///     per <c>RunAsync</c> (not per benchmark) after the suite finishes, carrying the run-end
///     sentinel plus a <see cref="MeasurementPhaseEvent.Succeeded" /> flag distinguishing a
///     clean completion from a harness-level crash.
/// </summary>
public enum MeasurementPhase
{
    /// <summary>Pre-flight environment probe. Reports a jitter metric; no body invocations.</summary>
    Jitter = 0,

    /// <summary>
    ///     Ops-per-sample (<c>K</c>) doubling search. Probes several candidate K values and
    ///     reports each as a warmup sample plus a detector update.
    /// </summary>
    Calibration = 1,

    /// <summary>
    ///     Warmup plateau. Discards samples until the body settles; reports each as a warmup
    ///     sample. Ends with a <see cref="WarmupStopReason" />.
    /// </summary>
    Warmup = 2,

    /// <summary>
    ///     Measurement. Collects the samples that feed statistics; reports each as a measured
    ///     sample plus a detector update with the live CI half-width. Ends with a
    ///     <see cref="SampleStopReason" />.
    /// </summary>
    Measurement = 3,

    /// <summary>
    ///     Suite/harness completed (or crashed). Emitted exactly once per <c>RunAsync</c> after
    ///     the suite finishes - on the success path before the method returns, and from the
    ///     <c>finally</c> with <see cref="MeasurementPhaseEvent.Succeeded" /> = <c>false</c> when
    ///     a harness-level exception prevented the success-path emit. The
    ///     <see cref="MeasurementPhaseEvent.BenchmarkName" /> is empty - this is a suite-level
    ///     event, not a per-benchmark one. Consumers (e.g. a live-streaming observer) treat it
    ///     as the authoritative run-end sentinel.
    /// </summary>
    SuiteCompleted = 4,
}

/// <summary>Whether a phase is starting or completed, reported on <see cref="MeasurementPhaseEvent" />.</summary>
public enum PhaseTransition
{
    /// <summary>The phase is about to run. The phase's outcome fields on the event are null.</summary>
    Starting = 0,

    /// <summary>The phase finished. The phase's outcome fields (jitter metric, K, stop reason) are populated.</summary>
    Completed = 1,
}

/// <summary>
///     A phase boundary event. <see cref="Transition" /> is <see cref="PhaseTransition.Starting" />
///     when the phase begins (outcome fields null) and <see cref="PhaseTransition.Completed" />
///     when it ends (outcome fields populated for that phase).
/// </summary>
/// <remarks>
///     <para>
///         Which outcome fields are set on completion depends on the phase: jitter reports
///         <see cref="JitterMetric" /> and <see cref="DetectorSwitched" />; calibration reports
///         <see cref="ResolvedK" />; warmup reports <see cref="WarmupStop" /> and
///         <see cref="ResolvedWarmup" />; measurement reports <see cref="SampleStop" />.
///         Fields not relevant to a phase are null.
///     </para>
///     <para>
///         <see cref="MeasurementPhase.SuiteCompleted" /> events use <see cref="Succeeded" />:
///         <c>true</c> on the success-path emit, <c>false</c> when emitted from the
///         <c>finally</c> after a harness-level exception. The <see cref="BenchmarkName" /> is
///         empty for suite-completed events.
///     </para>
/// </remarks>
public readonly record struct MeasurementPhaseEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    PhaseTransition Transition,
    double? JitterMetric = null,
    bool DetectorSwitched = false,
    int? ResolvedK = null,
    int? ResolvedWarmup = null,
    WarmupStopReason? WarmupStop = null,
    SampleStopReason? SampleStop = null,
    bool Succeeded = true);

/// <summary>
///     One timed sample. Emitted between samples, outside the timed region. The
///     <see cref="Warmup" /> flag is <c>true</c> for calibration and warmup samples and
///     <c>false</c> for measured samples, so a consumer can plot the warmup-settling curve
///     alongside the measured stream.
/// </summary>
/// <param name="Ordinal">The 0-based index of this sample within its phase.</param>
/// <param name="PerOpNs">The per-op nanoseconds of this sample: elapsed / <paramref name="K" />.</param>
/// <param name="K">The ops-per-sample count in effect when this sample was timed.</param>
/// <param name="AllocDelta">The allocation delta of this sample in bytes (per K ops), or 0 when allocation tracking is off.</param>
/// <param name="Warmup"><c>true</c> for calibration and warmup samples; <c>false</c> for measured samples.</param>
public readonly record struct SampleEvent(
    string BenchmarkName,
    int Ordinal,
    double PerOpNs,
    int K,
    long AllocDelta,
    bool Warmup);

/// <summary>
///     A snapshot of the running detector state. Emitted after a detector update so a
///     consumer can plot the autotune convergence curve live: running mean, sample standard
///     deviation, confidence-interval half-width, sample count, and current
///     ops-per-sample (<c>K</c>).
/// </summary>
/// <remarks>
///     During calibration, <see cref="MeanNs" />/<see cref="StdDev" />/<see cref="CiHalfWidth" />
///     reflect the calibrator's probe readings (the CI fields are not meaningful until
///     measurement). During measurement, <see cref="CiHalfWidth" /> is the live
///     convergence curve - the single most useful "why did it stop" signal.
/// </remarks>
public readonly record struct DetectorStateEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    int SampleCount,
    double MeanNs,
    double StdDev,
    double CiHalfWidth,
    int CurrentK);
