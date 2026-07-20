namespace NBenchmark;

/// <summary>
///     A machine-readable record of what the adaptive measurement loop decided for one
///     benchmark and why. Present on every measured <see cref="BenchmarkResult" /> (including
///     fully pinned runs, where the stop reasons are <c>ExplicitCount</c>); <c>null</c> on
///     dry-run and errored results, where nothing was measured.
/// </summary>
public sealed record AutoTuneDiagnostic
{
    /// <summary>The number of warmup samples that were discarded before measurement.</summary>
    public required int ResolvedWarmup { get; init; }

    /// <summary>The number of measured samples collected, before outlier trimming.</summary>
    public required int ResolvedSamples { get; init; }

    /// <summary>The ops-per-sample count (<c>K</c>): how many back-to-back body invocations made up one timed sample.</summary>
    public required int OpsPerSample { get; init; }

    /// <summary>
    ///     The ops-per-sample count that cold calibration (Phase A) originally resolved, when the loop
    ///     later <em>recalibrated</em> <c>K</c> upward from the warm per-op estimate measured during
    ///     warmup; <c>null</c> when no post-warmup recalibration occurred. When set,
    ///     <see cref="OpsPerSample" /> is the final (post-recalibration) <c>K</c> and this is the
    ///     pre-recalibration value - the gap reflects how much faster the warm body ran than the cold
    ///     code calibration first saw.
    /// </summary>
    public int? InitialOpsPerSample { get; init; }

    /// <summary>
    ///     The total number of body invocations across every phase of the loop &#8212; ops-per-sample
    ///     calibration, warmup, and measurement &#8212; counting each timed and untimed sample at the
    ///     ops-per-sample count in effect when it ran.
    /// </summary>
    public required long TotalBodyInvocations { get; init; }

    /// <summary>Why the warmup phase stopped.</summary>
    public required WarmupStopReason WarmupStop { get; init; }

    /// <summary>Why the measurement phase stopped.</summary>
    public required SampleStopReason SampleStop { get; init; }

    /// <summary>
    ///     The relative confidence-interval half-width achieved at stop, computed on the raw
    ///     (untrimmed) measured stream. The reported interval on <see cref="BenchmarkResult" /> is
    ///     computed on the trimmed samples and may differ slightly.
    /// </summary>
    public required double AchievedRelativeCiWidth { get; init; }

    /// <summary>
    ///     The wall-clock time spent in the adaptive loop itself (calibration, warmup, and
    ///     measurement) for this benchmark, excluding the runner's surrounding setup and progress
    ///     callbacks.
    /// </summary>
    public required TimeSpan TuningWallClock { get; init; }

    /// <summary>
    ///     The pre-flight jitter metric: the ratio of the median absolute deviation to the
    ///     median (MAD / median) of the per-sample timings of a deterministic busy-weight loop
    ///     run before calibration. The metric is robust - both the median and MAD have a ~50%
    ///     breakdown point, so a single JIT spike or one-off preemption cannot distort it. A
    ///     quiet dedicated host reports well below <c>0.05</c>; a shared-tenant CI runner
    ///     typically reports <c>0.10</c>-<c>0.30</c>. <c>null</c> when jitter calibration was
    ///     disabled (<see cref="AutoTuneOptions.EnableJitterCalibration" /> = <c>false</c>) or
    ///     the probe did not produce enough samples to compute a metric.
    ///     <para>
    ///         Use this to decide whether a result is trustworthy on a given host: a high
    ///         metric means the timings were collected under scheduling pressure and the
    ///         reported Error likely underestimates the true spread.
    ///     </para>
    /// </summary>
    public double? JitterMetric { get; init; }

    /// <summary>
    ///     <c>true</c> when the loop auto-switched the outlier detector from the configured
    ///     <c>IqrFence</c> to <c>MedianAbsoluteDeviation</c> because the
    ///     <see cref="JitterMetric" /> exceeded <see cref="AutoTuneOptions.JitterAutoSwitchThreshold" />.
    ///     <c>false</c> when the configured detector was used unchanged. The detector name in
    ///     use is on <see cref="BenchmarkResult.OutlierDetector" />.
    /// </summary>
    public bool OutlierDetectorSwitched { get; init; }
}
