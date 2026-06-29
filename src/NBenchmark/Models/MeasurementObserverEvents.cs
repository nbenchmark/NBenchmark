namespace NBenchmark;

/// <summary>
///     The phases of the adaptive measurement loop, reported on
///     <see cref="MeasurementPhaseEvent.Phase" /> and <see cref="SampleEvent" /> (via the
///     <see cref="SampleEvent.Warmup" /> flag). Each benchmark runs through them in order:
///     jitter, calibration, warmup, measurement. A pinned configuration short-circuits a
///     phase but still reports its boundary.
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
///     Which outcome fields are set on completion depends on the phase: jitter reports
///     <see cref="JitterMetric" /> and <see cref="DetectorSwitched" />; calibration reports
///     <see cref="ResolvedK" />; warmup reports <see cref="WarmupStop" /> and
///     <see cref="ResolvedWarmup" />; measurement reports <see cref="SampleStop" />.
///     Fields not relevant to a phase are null.
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
    SampleStopReason? SampleStop = null);

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
///     During calibration, <see cref="Mean" />/<see cref="StdDev" />/<see cref="CiHalfWidth" />
///     reflect the calibrator's probe readings (the CI fields are not meaningful until
///     measurement). During measurement, <see cref="CiHalfWidth" /> is the live
///     convergence curve - the single most useful "why did it stop" signal.
/// </remarks>
public readonly record struct DetectorStateEvent(
    string BenchmarkName,
    MeasurementPhase Phase,
    int SampleCount,
    double Mean,
    double StdDev,
    double CiHalfWidth,
    int CurrentK);