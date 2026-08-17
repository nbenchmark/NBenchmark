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

    /// <summary>
    ///     The relative CI half-width at each evaluation point during the measurement phase, in
    ///     evaluation order. One entry per cadence check once past
    ///     <see cref="AutoTuneOptions.MinSamples" />, plus a final entry when
    ///     <see cref="AutoTuneOptions.MaxSamples" /> is reached. Empty for pinned runs (no adaptive
    ///     convergence) and for autotuned runs that never reached the evaluation floor.
    ///     <para>
    ///         The final entry may differ slightly from <see cref="AchievedRelativeCiWidth" />,
    ///         which is recomputed on the full raw sample set after the loop stops: the series uses
    ///         the Welford accumulator's running stats at the moment of each evaluation, while the
    ///         scalar is computed from the complete sample array. The series shows the convergence
    ///         trajectory, the scalar shows the final achieved value.
    ///     </para>
    ///     <para>
    ///         When <see cref="MeasurementRestarts" /> is non-zero the series covers only the final
    ///         attempt - the convergence trajectory of the samples actually reported.
    ///     </para>
    /// </summary>
    public IReadOnlyList<double> CiWidthSeries { get; init; } = [];

    /// <summary>
    ///     Whether auto-warmup reached <see cref="AutoTuneOptions.MinWarmupTime" /> before it stopped.
    ///     <c>false</c> means warmup was cut short - by <see cref="AutoTuneOptions.MaxWarmup" />, by the
    ///     calibration+warmup budget share, or because the body is calibration-ineligible and too fast
    ///     to reach the floor within the sample ceiling - so the body may still have been running
    ///     pre-tier-1 code when measurement began.
    ///     <para>
    ///         This is the single most useful field for diagnosing a benchmark whose median differs
    ///         wildly between runs while each run reports a tight error margin. Always <c>true</c> for a
    ///         pinned <see cref="MeasurementOptions.WarmupIterations" /> (the floor does not apply).
    ///     </para>
    /// </summary>
    public bool WarmupTimeFloorMet { get; init; } = true;

    /// <summary>
    ///     How many methods the JIT compiled over the course of auto-warmup, sampled at batch
    ///     boundaries. <c>0</c> for pinned warmup, and for auto-warmup that ended before its first
    ///     batch boundary. A large value next to a short warmup is the signature of a body measured
    ///     mid-tier-up.
    ///     <para>
    ///         Process-wide, not per-benchmark: in an in-process run the first benchmark to execute
    ///         absorbs the bulk of startup compilation and later ones see almost none. Since benchmark
    ///         order is randomised, this is also a large part of why the same benchmark's warmup cost
    ///         differs from run to run.
    ///     </para>
    /// </summary>
    public long WarmupJitCompiledMethods { get; init; }

    /// <summary>
    ///     Wall-clock time the JIT spent compiling during auto-warmup. Unlike
    ///     <see cref="WarmupJitCompiledMethods" /> this is denominated in the same units as the
    ///     benchmark itself, which makes it the honest answer to "what did tiered compilation cost
    ///     here?". <see cref="TimeSpan.Zero" /> for pinned warmup.
    /// </summary>
    public TimeSpan WarmupJitCompilationTime { get; init; }

    /// <summary>
    ///     IL bytes the JIT compiled during auto-warmup. Distinguishes "a few large methods" from
    ///     "many small ones" when reading <see cref="WarmupJitCompiledMethods" />.
    /// </summary>
    public long WarmupJitCompiledIlBytes { get; init; }

    /// <summary>
    ///     How far into warmup, in nanoseconds, the JIT compiled-method count last changed - or
    ///     <c>0</c> when it never did. With the body under continuous load the last compilation is
    ///     typically the promotion of its own hot path, which makes this the engine's closest
    ///     approximation of a tier-up landing marker. Compare against
    ///     <see cref="WarmupElapsedNs" /> to see how much quiet time followed.
    /// </summary>
    public double JitLastChangeAtNs { get; init; }

    /// <summary>
    ///     Total elapsed nanoseconds across every auto-warmup sample. The x-axis extent for
    ///     <see cref="WarmupCurve" /> and the denominator for
    ///     <see cref="AutoTuneOptions.MinWarmupTime" />.
    /// </summary>
    public double WarmupElapsedNs { get; init; }

    /// <summary>
    ///     Whether warmup ended with the JIT genuinely quiet - the configured
    ///     <see cref="AutoTuneOptions.JitQuietPeriod" /> elapsed with no compilation - rather than the
    ///     gate having been bypassed by its deactivation threshold. <c>false</c> means measurement may
    ///     have started while compilation was still in flight.
    /// </summary>
    public bool JitQuiescenceAchieved { get; init; } = true;

    /// <summary>
    ///     The warmup curve: the mean per-op nanoseconds of each auto-warmup batch, oldest first.
    ///     Empty for pinned warmup and for warmup that ended before its first batch boundary.
    ///     <para>
    ///         This is where tiered compilation is visible. The body starts in tier-0 or quick-jitted
    ///         code, the runtime promotes it after the call-counting delay, dynamic PGO may instrument
    ///         and re-optimise it, and each transition shows up as a step down in per-op time. Plotted
    ///         against <see cref="WarmupSampleInterval" /> and marked with
    ///         <see cref="JitLastChangeAtNs" />, it is the tier-up story for this benchmark.
    ///     </para>
    ///     <para>
    ///         Batch means rather than raw samples: they are what the plateau rule already computes,
    ///         and the averaging keeps a two-or-three-step decay from being buried in jitter. Bounded
    ///         in length - long warmups are decimated by a doubling stride, so the shape survives at
    ///         coarser resolution.
    ///     </para>
    /// </summary>
    public IReadOnlyList<double> WarmupCurve { get; init; } = [];

    /// <summary>
    ///     Warmup samples between consecutive <see cref="WarmupCurve" /> points, so a caller can label
    ///     a real iteration axis. <c>0</c> when the curve is empty.
    /// </summary>
    public int WarmupSampleInterval { get; init; }

    /// <summary>
    ///     How many times the drift gate discarded the collected samples and restarted measurement
    ///     because the stream had not settled (see
    ///     <see cref="AutoTuneOptions.MeasurementDriftTolerance" />). <c>0</c> for the overwhelming
    ///     majority of runs; a non-zero value means a step change - usually a JIT tier-up or a dynamic
    ///     PGO re-optimization - landed inside the measurement window and the loop resampled past it.
    /// </summary>
    public int MeasurementRestarts { get; init; }

    /// <summary>
    ///     The relative gap between the means of the first and second halves of the reported samples,
    ///     as a fraction of the smaller half-mean. Near <c>0</c> for a stationary body.
    ///     <para>
    ///         Computed on every stop, not only when the drift gate fires, so drift stays visible on
    ///         pinned-count, wall-clock-cap, and ceiling stops - none of which consult the gate. A large
    ///         value alongside a narrow <see cref="AchievedRelativeCiWidth" /> means the interval is
    ///         tight around a centre that moved: the reported number is precise but not reproducible.
    ///     </para>
    /// </summary>
    public double SplitHalfDrift { get; init; }

    /// <summary>
    ///     The measured effective resolution of the measurement clock, in nanoseconds: the smallest
    ///     non-zero interval the engine can observe. <c>0</c> when the probe could not determine it.
    ///     <para>
    ///         Measured, not read from <c>Stopwatch.Frequency</c>, because the advertised rate can be
    ///         badly wrong - Apple Silicon reports 1 GHz while its timebase steps in 41.667 ns units.
    ///         Typical values: ~20-40 ns on Apple Silicon, ~100 ns on Windows QPC, a few nanoseconds on
    ///         a TSC-backed Linux host.
    ///     </para>
    /// </summary>
    public double ClockResolutionNs { get; init; }

    /// <summary>
    ///     The sample-duration target ops-per-sample calibration actually resolved against, after
    ///     <see cref="AutoTuneOptions.MinQuantaPerSample" /> raised
    ///     <see cref="AutoTuneOptions.TargetSampleDurationNs" /> to clear the clock's resolution. Equal
    ///     to the configured target when the host's clock was already fine enough, or when the floor is
    ///     disabled.
    /// </summary>
    public double TargetSampleDurationNs { get; init; }

    /// <summary>
    ///     What one timed sample actually spanned, in nanoseconds: the achieved per-op mean times
    ///     <see cref="OpsPerSample" />. Overshoots <see cref="TargetSampleDurationNs" /> because
    ///     <c>K</c> doubles, so the resolved value is the first power of two past the target.
    /// </summary>
    public double SampleDurationNs { get; init; }

    /// <summary>
    ///     One clock-resolution step as a fraction of one timed sample - the granularity floor on how
    ///     finely this measurement could be resolved, whatever the reported margin says. <c>0</c> when
    ///     the clock resolution is unknown.
    ///     <para>
    ///         Read this against the reported error margin. A margin well below this fraction is
    ///         describing the clock's step grid rather than the code: within a run consecutive samples of
    ///         a stable body land on the same step, so the spread looks tiny, while between runs a shift
    ///         far smaller than one step moves every sample to the next step and the median with it. That
    ///         combination - a margin of ±0.03% and a median that moves 0.5% on re-run - is the
    ///         signature, and it is indistinguishable from a genuine result without this field.
    ///     </para>
    ///     <para>
    ///         Alongside <see cref="WarmupTimeFloorMet" />, this is the field to reach for when a
    ///         benchmark reports a tight margin and will not reproduce. That one covers a body measured
    ///         before tiered compilation finished; this one covers a body measured finer than the timer
    ///         can see.
    ///     </para>
    /// </summary>
    public double SampleQuantizationFraction { get; init; }

    /// <summary>
    ///     How many measured samples the evidence-based interference filter rejected as confirmed OS
    ///     preemption - a per-sample CPU-occupancy ratio materially below this benchmark's own
    ///     median (see <see cref="InterferenceOptions" />) - before the statistical outlier detector
    ///     ran. <c>0</c> when nothing was rejected, including when the filter is disabled (see
    ///     <see cref="InterferenceDisabledReason" />) or the run was too quiet to need it.
    /// </summary>
    public int InterferenceRejectedCount { get; init; }

    /// <summary>
    ///     This benchmark's own median CPU-occupancy ratio (<c>cpuDelta / wallDelta</c>) among
    ///     samples with a known reading - the value <see cref="InterferenceRejectedCount" /> was
    ///     computed against. <c>null</c> when the filter did not run (see
    ///     <see cref="InterferenceDisabledReason" />). Not comparable across platforms: Linux and
    ///     macOS report the CPU side in nanoseconds, Windows in cycles, so only ratios computed
    ///     within the same run are meaningful.
    /// </summary>
    public double? MedianOccupancyRatio { get; init; }

    /// <summary>
    ///     Why the interference filter did not reject anything on its own initiative for this
    ///     benchmark, or <c>null</c> when it ran normally (whether or not it found anything to
    ///     reject). Set when: the thread-CPU clock is unavailable on this platform; two clock reads
    ///     cost more than <see cref="InterferenceOptions.MaxProbeCostFraction" /> of the resolved
    ///     sample-duration target, so the probe was disabled for this run before it started; or too
    ///     few samples carried a known occupancy reading to trust a median - typically an async body
    ///     whose continuations mostly resumed on a different thread.
    /// </summary>
    public string? InterferenceDisabledReason { get; init; }
}
