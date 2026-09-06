namespace NBenchmark;

/// <summary>
///     Tuning knobs for the adaptive measurement loop. Each dimension of the loop
///     (<see cref="MeasurementOptions.OpsPerSample">ops per sample</see>, warmup length, and
///     measured-sample count) is auto-resolved at runtime unless pinned to an explicit value;
///     these options bound and steer that resolution.
/// </summary>
/// <remarks>
///     The <see cref="MeasurementOptions.ConfidenceLevel" /> used for the reported interval is
///     also the confidence level the CI-width stop rule targets, so it is not duplicated here.
/// </remarks>
public sealed record AutoTuneOptions
{
    /// <summary>The balanced default profile.</summary>
    public static readonly AutoTuneOptions Default = new();

    /// <summary>
    ///     Fewer samples and a looser CI target for fast feedback.
    ///     <para>
    ///         Note this preset does <em>not</em> shorten <see cref="MinWarmupTime" />. That floor is a
    ///         measurement-<em>correctness</em> requirement (it is what gives tiered compilation time to
    ///         land before measurement starts), not a speed/accuracy trade-off, so it is the same across
    ///         every preset. Quick's speed comes from its looser <see cref="CiTarget" />, lower
    ///         <see cref="MinSamples" />, and shorter <see cref="MaxTuningTime" />.
    ///     </para>
    /// </summary>
    public static readonly AutoTuneOptions Quick = new()
    {
        MinWarmup = 4,
        MinSamples = 15,
        MaxSamples = 2_000,
        CiTarget = 0.05,
        MaxTuningTime = TimeSpan.FromSeconds(10),
        BatchSize = 4,
        PlateauPatience = 2,
        MinMeasurementTime = TimeSpan.FromMilliseconds(50),
    };

    /// <summary>More samples and a tighter CI target for publication-grade numbers.</summary>
    public static readonly AutoTuneOptions Thorough = new()
    {
        MinWarmup = 16,
        MinSamples = 100,
        MaxSamples = 20_000,
        CiTarget = 0.01,
        TargetSampleDurationNs = 50_000,
        MaxTuningTime = TimeSpan.FromSeconds(60),
        MinWarmupTime = TimeSpan.FromMilliseconds(1_000),
        JitQuietPeriod = TimeSpan.FromMilliseconds(100),
        MinMeasurementTime = TimeSpan.FromMilliseconds(500),
        MeasurementRestartLimit = 3,
    };

    private readonly int _batchSize = 8;
    private readonly int _maxOpsPerSample = 1 << 20;
    private readonly int _plateauPatience = 3;
    private readonly double _warmupBudgetFraction = 0.4;
    private readonly double _capGraceFactor = 1.5;
    private readonly TimeSpan _minWarmupTime = TimeSpan.FromMilliseconds(500);
    private readonly TimeSpan _jitQuietPeriod = TimeSpan.FromMilliseconds(50);
    private readonly TimeSpan _minMeasurementTime = TimeSpan.FromMilliseconds(100);
    private readonly double _measurementDriftTolerance = 0.10;
    private readonly int _measurementRestartLimit = 2;
    private readonly int _minQuantaPerSample = 512;

    // ----- Warmup plateau -----

    /// <summary>
    ///     The earliest sample at which auto-warmup may settle. Default 8.
    ///     <para>
    ///         This is a floor on the sample <em>count</em>. Note the plateau rule cannot settle
    ///         before it has seen <c>(PlateauPatience + 1) × BatchSize</c> samples (one improving
    ///         batch plus <see cref="PlateauPatience" /> non-improving ones), so with the defaults
    ///         (patience 3, batch 8) the effective minimum is 32 samples and <c>MinWarmup</c> only
    ///         binds when raised above that. <see cref="MinWarmupTime" /> is the independent floor
    ///         on warmup <em>duration</em>, which in practice is the binding one for almost every body.
    ///     </para>
    /// </summary>
    public int MinWarmup { get; init; } = 8;

    /// <summary>
    ///     The warmup ceiling, as a sample count. Default 100,000
    ///     (== <see cref="MeasurementOptions.MaxAutoWarmupIterations" />).
    ///     <para>
    ///         This is deliberately far above the count any body needs, so that the <em>time</em> bounds -
    ///         <see cref="MinWarmupTime" /> from below and the calibration+warmup share
    ///         (<see cref="WarmupBudgetFraction" /> of <see cref="MaxTuningTime" />) from above - are the
    ///         binding constraints. A count ceiling low enough to bind before
    ///         <see cref="MinWarmupTime" /> silently defeats the floor: warmup exits on the ceiling with
    ///         the body still running pre-tier-1 code. Hitting this ceiling before the time floor is
    ///         reached raises a warning.
    ///     </para>
    /// </summary>
    public int MaxWarmup { get; init; } = MeasurementOptions.MaxAutoWarmupIterations;

    /// <summary>
    ///     The minimum relative improvement a warmup batch must show over the best batch so far
    ///     to count as "still getting faster". Default 0.02 (2%).
    /// </summary>
    public double WarmupEpsilon { get; init; } = 0.02;

    /// <summary>
    ///     The number of consecutive non-improving batches that ends warmup. Must be at least 1.
    ///     Default 3.
    /// </summary>
    public int PlateauPatience
    {
        get => _plateauPatience;
        init => _plateauPatience = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "PlateauPatience must be at least 1.");
    }

    /// <summary>
    ///     The minimum in-body time auto-warmup must run before it may settle, regardless of how
    ///     quickly the plateau rule is satisfied. Default 500 ms (1 s under the <c>Thorough</c>
    ///     preset); the same on every preset, since this is a correctness floor rather than a
    ///     speed/accuracy trade-off.
    ///     <para>
    ///         The plateau rule measures warmup in <em>iterations</em>, but a fast body plateaus in
    ///         microseconds of wall-clock - long before the background JIT delivers tier-1 (and
    ///         dynamic-PGO) code. Warmup then settles on the stable but slow tier-0 plateau and the
    ///         tier-1 switch lands mid-measurement as a step change, the dominant source of
    ///         run-to-run variance on very fast benchmarks. This floor holds warmup open long enough
    ///         for tiered compilation to land.
    ///     </para>
    ///     <para>
    ///         The default is 5× the runtime's <c>TieredCompilation.CallCountingDelayMs</c> (100 ms).
    ///         That delay <em>restarts</em> while tier-0 methods are still being called for the first
    ///         time, and tier-1 is only <em>queued</em> once it finally expires - then compiled on a
    ///         background thread, with a further instrumented-to-optimized transition under dynamic PGO
    ///         and, for a method with a hot loop, on-stack replacement before any of that. A floor at or
    ///         below 100 ms therefore reliably lands those transitions inside the measurement window
    ///         instead of before it.
    ///     </para>
    ///     <para>
    ///         500 ms was chosen empirically, not from the delay alone. On a 10-benchmark suite, raising
    ///         the floor from 250 ms to 500 ms cost 55% more wall-clock and took the worst observed
    ///         run-to-run median spread from 4.8× to 1.08× (a <c>StringBuilder</c>-append loop whose
    ///         steady state is ~4.5× faster than its tier-0 code, and which at 250 ms landed in either
    ///         regime depending on the run). Going on to 1 s cost a further 76% and improved only one
    ///         remaining benchmark, so that is where <c>Thorough</c> sits rather than the default. A body
    ///         that needs longer still - typically a hot loop that depends heavily on dynamic PGO - shows
    ///         up as a stable but irreproducible median; raise this knob for it.
    ///     </para>
    ///     <para>
    ///         It is bounded above by the calibration+warmup budget share
    ///         (<see cref="WarmupBudgetFraction" /> of <see cref="MaxTuningTime" />) and by
    ///         <see cref="MaxWarmup" />, either of which stops warmup first for a genuinely slow body.
    ///         Set to <see cref="TimeSpan.Zero" /> to disable the floor (which also disables the
    ///         <see cref="RequireJitQuiescence" /> gate).
    ///     </para>
    /// </summary>
    public TimeSpan MinWarmupTime
    {
        get => _minWarmupTime;
        init => _minWarmupTime = value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MinWarmupTime must be zero or positive.");
    }

    /// <summary>
    ///     Whether auto-warmup additionally refuses to settle until the JIT has been quiet for
    ///     <see cref="JitQuietPeriod" /> (a proxy for in-flight tier-1 promotion of the body under
    ///     test having completed). Default <c>true</c>.
    ///     <para>
    ///         At each warmup batch boundary the loop reads <see cref="System.Runtime.JitInfo" />'s
    ///         compiled-method count and remembers where in warmup it last changed; warmup continues
    ///         until that change is <see cref="JitQuietPeriod" /> in the past. To avoid blocking
    ///         forever on a busy in-process host that JITs unrelated code, the gate deactivates once
    ///         warmup has run for 4 × <see cref="MinWarmupTime" />. The gate is inactive when
    ///         <see cref="MinWarmupTime" /> or <see cref="JitQuietPeriod" /> is
    ///         <see cref="TimeSpan.Zero" />. Set to <c>false</c> to keep only the time floor.
    ///     </para>
    /// </summary>
    public bool RequireJitQuiescence { get; init; } = true;

    /// <summary>
    ///     How long the JIT compiled-method count must stay unchanged before the
    ///     <see cref="RequireJitQuiescence" /> gate lets warmup settle, measured in accumulated in-body
    ///     warmup time. Default 50 ms (100 ms under the <c>Thorough</c> preset).
    ///     <para>
    ///         A <em>sustained quiet interval</em> is the point of this knob. Asking only whether the
    ///         JIT compiled anything during the most recent batch cannot work: for a fast body one
    ///         batch spans tens of microseconds, so a background tier-1 compilation almost never lands
    ///         inside that specific window and the per-batch delta reads zero essentially always. The
    ///         quiet interval has to be matched to the timescale of the phenomenon, not to the batch.
    ///     </para>
    ///     <para>
    ///         Clamped down to <see cref="MinWarmupTime" /> by the detector so it can never become the
    ///         binding floor - when nothing is compiling, warmup ends at <see cref="MinWarmupTime" />;
    ///         when something is, warmup extends until the quiet interval elapses, bounded by
    ///         4 × <see cref="MinWarmupTime" />. Set to <see cref="TimeSpan.Zero" /> to disable the
    ///         gate while keeping the time floor.
    ///     </para>
    /// </summary>
    public TimeSpan JitQuietPeriod
    {
        get => _jitQuietPeriod;
        init => _jitQuietPeriod = value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "JitQuietPeriod must be zero or positive.");
    }

    // ----- CI-width sample count -----

    /// <summary>
    ///     The earliest sample <em>count</em> at which auto-measurement may stop on the CI target.
    ///     Default 30 (15 under <c>Quick</c>, 100 under <c>Thorough</c>).
    ///     <para>
    ///         This is the <em>validity</em> floor - below it the computed interval is not trustworthy
    ///         regardless of how narrow it looks. <see cref="MinMeasurementTime" /> is the independent
    ///         floor on measurement <em>duration</em>, which for fast bodies is the binding one and is
    ///         what makes the sample count scale with how cheap the body is. This count floor is also
    ///         the one the <see cref="CapGraceFactor" /> grace budget chases, so it stays a count.
    ///     </para>
    /// </summary>
    public int MinSamples { get; init; } = 30;

    /// <summary>
    ///     The measured-sample ceiling. Default 5,000 (2,000 under <c>Quick</c>, 20,000 under
    ///     <c>Thorough</c>).
    ///     <para>
    ///         At 5,000 samples the CI-width rule still delivers ±2.5% for any body with a coefficient
    ///         of variation up to roughly 90%. Past that, the required count grows as
    ///         <c>(t × CV / target)²</c> and runs away - a CV of 580% needs ~50,000 samples to reach
    ///         ±5% - but a body that noisy has variance that <em>is</em> the finding, and more samples
    ///         only buy a tighter interval around an unstable centre. Hitting this ceiling with the CI
    ///         target unmet raises a warning that reports the CV and suggests
    ///         <c>--launch-count</c> instead.
    ///     </para>
    ///     <para>
    ///         Also the point at which <see cref="MinMeasurementTime" /> stops waiting for its duration,
    ///         so a body too fast to accumulate that duration is bounded here rather than chasing a
    ///         target it can never reach.
    ///     </para>
    /// </summary>
    public int MaxSamples { get; init; } = 5_000;

    /// <summary>
    ///     The target relative half-width of the confidence interval on the mean. Measurement
    ///     stops once the achieved half-width divided by the mean falls below this. Default 0.025
    ///     (±2.5%).
    /// </summary>
    public double CiTarget { get; init; } = 0.025;

    /// <summary>
    ///     The minimum in-body time the measurement phase must span before it may stop on the CI
    ///     target. Default 100 ms (50 ms under <c>Quick</c>, 500 ms under <c>Thorough</c>).
    ///     <para>
    ///         The measurement analogue of <see cref="MinWarmupTime" />, and the reason the resolved
    ///         sample count scales with body speed instead of being a flat number. A flat
    ///         <see cref="MinSamples" /> is blind to how cheap the body is: the same 30 samples that
    ///         cost 9 s on a 300 ms body cost 0.5 ms on a 1 µs body, where thousands of samples are
    ///         essentially free and buy meaningful percentiles, a usable histogram, and a significance
    ///         test with real power. At n ≈ 16 the reported p95/p99/p99.9 all collapse onto the maximum.
    ///     </para>
    ///     <para>
    ///         The rule is simply: measurement spans at least this long, or reaches
    ///         <see cref="MaxSamples" /> samples, whichever comes first. So the worst-case added cost is
    ///         <see cref="MinMeasurementTime" /> per benchmark, and it is <b>zero</b> for any body
    ///         already slower than <c>MinMeasurementTime / MinSamples</c> (about 3.3 ms at the defaults),
    ///         where <see cref="MinSamples" /> binds and nothing changes. Set to
    ///         <see cref="TimeSpan.Zero" /> to disable the floor and stop on <see cref="MinSamples" />
    ///         alone.
    ///     </para>
    /// </summary>
    public TimeSpan MinMeasurementTime
    {
        get => _minMeasurementTime;
        init => _minMeasurementTime = value >= TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MinMeasurementTime must be zero or positive.");
    }

    /// <summary>
    ///     How far the first and second halves of the measured samples may disagree before the loop
    ///     refuses to stop on the CI target, as a fraction of the smaller half-mean. Default 0.10 (10%).
    ///     <para>
    ///         Guards the failure mode that makes a run-to-run discrepancy hardest to notice: a JIT
    ///         tier-up (or thermal ramp, or cache fill) landing inside the measurement window produces
    ///         a step change, and the CI-on-the-mean rule is perfectly happy to report a tight interval
    ///         across it. Without this gate the loop can report ±0.9% on a number that is 10× wrong.
    ///     </para>
    ///     <para>
    ///         The check also requires the gap to be statistically real, not just large, so a
    ///         heavy-tailed body whose half-means differ by pure sampling noise is not flagged. When it
    ///         does fire, the stale samples are discarded and measurement restarts (up to
    ///         <see cref="MeasurementRestartLimit" /> times). Set to <c>0</c> to disable the gate.
    ///     </para>
    /// </summary>
    public double MeasurementDriftTolerance
    {
        get => _measurementDriftTolerance;
        init => _measurementDriftTolerance = value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MeasurementDriftTolerance must be in [0, 1].");
    }

    /// <summary>
    ///     How many times the drift gate (<see cref="MeasurementDriftTolerance" />) may discard the
    ///     collected samples and restart measurement. Default 2 (3 under <c>Thorough</c>).
    ///     <para>
    ///         Two restarts covers the two transitions a .NET body normally goes through: tier-0 to
    ///         tier-1, and instrumented to optimized under dynamic PGO. A body still drifting after
    ///         that is genuinely non-stationary (thermal ramp, filling cache, growing data structure),
    ///         which is a finding rather than something more restarts fix - so the loop stops and
    ///         reports <see cref="SampleStopReason.DriftUnresolved" /> with a warning.
    ///     </para>
    ///     <para>
    ///         Restarts draw on the same <see cref="MaxTuningTime" /> budget as ordinary sampling, so
    ///         they can never extend a benchmark's total runtime. Set to <c>0</c> to report
    ///         <see cref="SampleStopReason.DriftUnresolved" /> on the first detected drift instead of
    ///         resampling.
    ///     </para>
    /// </summary>
    public int MeasurementRestartLimit
    {
        get => _measurementRestartLimit;
        init => _measurementRestartLimit = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MeasurementRestartLimit must be zero or positive.");
    }

    // ----- Ops-per-sample calibration -----

    /// <summary>
    ///     The target duration of a single timed sample, in nanoseconds. Auto-calibration doubles
    ///     the ops-per-sample count until a sample spans at least this long, amortising fixed
    ///     timer overhead. Default 10,000 (10 µs).
    ///     <para>
    ///         10 µs keeps the fixed <em>timestamp-read overhead</em> negligible (~10-30 ns per sample,
    ///         ~0.2% of 10 µs rather than ~1-3% of 1 µs), which would otherwise leak into the ±2.5% CI
    ///         target the calibration feeds. Bodies already spanning ≥ 10 µs keep <c>K = 1</c>, so their
    ///         per-op tail visibility is unchanged; only sub-10 µs bodies are batched (and for those,
    ///         percentiles describe batch means - see the docs).
    ///     </para>
    ///     <para>
    ///         This value is a <em>floor request</em>, not the final target. Clock <em>quantization</em>
    ///         is host-specific and cannot be covered by any single constant: 10 µs spans ~100,000 steps
    ///         of a 1 ns clock, ~240 steps of Apple Silicon's 41.667 ns timebase, and only ~100 steps of
    ///         Windows QPC's 100 ns tick. <see cref="MinQuantaPerSample" /> raises the effective target
    ///         per host from the clock's measured resolution, so quantization lands at a known fraction
    ///         of a sample everywhere instead of varying 1,000-fold across platforms.
    ///     </para>
    /// </summary>
    public double TargetSampleDurationNs { get; init; } = 10_000;

    /// <summary>
    ///     The minimum number of clock-resolution steps a single timed sample must span. The loop
    ///     measures the clock's effective resolution once per process
    ///     (<c>Engine/Detectors/ClockResolutionProbe</c>) and raises
    ///     <see cref="TargetSampleDurationNs" /> to <c>resolution × MinQuantaPerSample</c> when the
    ///     configured target would not clear it. The target is only ever raised. Default 512.
    ///     <para>
    ///         512 steps puts one step at under 0.2% of a sample, which is roughly a twelfth of the
    ///         default ±2.5% <see cref="CiTarget" /> - small enough not to contaminate the interval,
    ///         while keeping samples short enough that a cheap body still collects thousands of them
    ///         within <see cref="MinMeasurementTime" />. On Apple Silicon this resolves to ~21 µs
    ///         (up from the configured 10 µs); on Windows QPC to ~51 µs; on a TSC-backed Linux host the
    ///         configured 10 µs already clears it and nothing changes.
    ///     </para>
    ///     <para>
    ///         Quantization matters because of how asymmetrically it presents. Within one run, a stable
    ///         body's samples land on the same step, so the spread looks tiny and the reported margin
    ///         collapses toward zero. Between runs, a shift far smaller than one step moves every sample
    ///         to the next step and takes the median with it. The result is a measurement that appears
    ///         precise to three decimal places and moves by a whole step when re-run - which is exactly
    ///         the pattern this floor exists to prevent, and why raising it cannot be traded off against
    ///         speed the way <see cref="CiTarget" /> can.
    ///     </para>
    ///     <para>
    ///         Set to <c>0</c> to disable the floor and make <see cref="TargetSampleDurationNs" />
    ///         authoritative on every host. Raising it much past 512 is usually counterproductive: it
    ///         buys little further resolution and lengthens samples until genuine machine noise
    ///         (preemption, frequency shifts) starts landing inside the timed window.
    ///     </para>
    /// </summary>
    public int MinQuantaPerSample
    {
        get => _minQuantaPerSample;
        init => _minQuantaPerSample = value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MinQuantaPerSample must be zero or positive.");
    }

    /// <summary>The ceiling on auto-calibrated ops per sample. Must be at least 1. Default 2^20.</summary>
    public int MaxOpsPerSample
    {
        get => _maxOpsPerSample;
        init => _maxOpsPerSample = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxOpsPerSample must be at least 1.");
    }

    // ----- Shared -----

    /// <summary>
    ///     The number of samples grouped into one warmup batch and the cadence on which the
    ///     CI-width stop rule is evaluated. Must be at least 1. Default 8.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        init => _batchSize = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "BatchSize must be at least 1.");
    }

    /// <summary>
    ///     The per-benchmark safety cap on cumulative in-body sample time: the loop stops once the
    ///     measured nanoseconds of the timed samples (calibration, warmup, and measurement) reach
    ///     this budget. Setup, teardown, GC, and timer overhead are excluded, so the real wall-clock
    ///     for a benchmark can exceed this value. Default 20 s.
    /// </summary>
    public TimeSpan MaxTuningTime { get; init; } = TimeSpan.FromSeconds(20);

    // ----- Phase 0: pre-flight jitter calibration -----

    /// <summary>
    ///     Whether the adaptive loop runs a pre-flight jitter probe before calibration. The
    ///     probe times a deterministic busy-weight loop and derives a <see cref="AutoTuneDiagnostic.JitterMetric" />
    ///     (the ratio of the median absolute deviation to the median, MAD / median, of per-sample
    ///     wall-clock) that characterises the host's baseline interruption rate. When the metric exceeds
    ///     <see cref="JitterAutoSwitchThreshold" /> and the user has not pinned an outlier
    ///     detector, the loop switches the effective detector from <c>IqrFence</c> to
    ///     <c>MedianAbsoluteDeviation</c> for that run - MAD's ~50% breakdown point is more
    ///     resilient than IQR to the heavy-tailed, skewed samples a noisy host produces.
    ///     <para>
    ///         Defaults to <c>true</c>. Set to <c>false</c> to skip the probe entirely (the
    ///         jitter metric is reported as <c>null</c> and the detector is never auto-switched).
    ///         Pinning <see cref="MeasurementOptions.OutlierMode" /> or
    ///         <see cref="MeasurementOptions.OutlierDetector" /> disables the auto-switch but
    ///         not the probe - the metric is still reported for visibility.
    ///     </para>
    /// </summary>
    public bool EnableJitterCalibration { get; init; } = true;

    /// <summary>
    ///     The number of timed samples the jitter probe collects. Each sample runs
    ///     <see cref="JitterCalibrationWorkPerSample" /> busy-weight iterations. Default 32 -
    ///     enough to characterise the tail without measurably extending the tuning budget.
    /// </summary>
    public int JitterCalibrationSamples { get; init; } = 32;

    /// <summary>
    ///     The number of deterministic arithmetic iterations each jitter sample performs. The
    ///     loop body is a multiply-accumulate over a private accumulator, chosen to be
    ///     CPU-bound, allocation-free, and not optimised away. Default 4096 - spans a few
    ///     microseconds on modern hardware, long enough to observe a scheduling preemption
    ///     but short enough that 32 samples complete in single-digit milliseconds.
    /// </summary>
    public int JitterCalibrationWorkPerSample { get; init; } = 4096;

    /// <summary>
    ///     The jitter metric value above which the loop auto-switches the outlier detector
    ///     from <c>IqrFence</c> to <c>MedianAbsoluteDeviation</c>. The metric is the ratio of
    ///     the median absolute deviation to the median (MAD / median) of the per-sample
    ///     busy-weight timings; a quiet host reports well below 0.05, a shared-tenant CI
    ///     runner typically reports 0.10-0.30. Default 0.10 - switches only on genuinely noisy
    ///     hosts. Set to a non-positive value (<c>0</c> or negative) to disable the
    ///     auto-switch while still running the probe and reporting the metric.
    /// </summary>
    public double JitterAutoSwitchThreshold { get; init; } = 0.10;

    /// <summary>
    ///     What happens when the adaptive loop stops because it hit the wall-clock tuning cap
    ///     before reaching the confidence-interval target or a steady warmup state. Default
    ///     <see cref="AutoTuneCapBehavior.Warn" />.
    /// </summary>
    public AutoTuneCapBehavior CapBehavior { get; init; } = AutoTuneCapBehavior.Warn;

    /// <summary>
    ///     The maximum share of <see cref="MaxTuningTime" /> that ops-per-sample calibration and
    ///     warmup may consume together. The remaining share is reserved for measurement. Must be
    ///     strictly greater than 0 and at most 1. Default 0.4 (40%).
    /// </summary>
    public double WarmupBudgetFraction
    {
        get => _warmupBudgetFraction;
        init => _warmupBudgetFraction = value is > 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "WarmupBudgetFraction must be in (0, 1].");
    }

    /// <summary>
    ///     The hard ceiling multiplier the measurement phase may reach while chasing
    ///     <see cref="MinSamples" /> after the wall-clock cap fires. When the cap fires before
    ///     <see cref="MinSamples" /> is reached, the loop keeps sampling up to
    ///     <c>MaxTuningTime * CapGraceFactor</c> rather than stop on a dangerously
    ///     under-sampled result. Must be at least 1. Default 1.5.
    /// </summary>
    public double CapGraceFactor
    {
        get => _capGraceFactor;
        init => _capGraceFactor = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "CapGraceFactor must be at least 1.");
    }

    /// <summary>Resolves a <see cref="AutoTunePreset" /> to its concrete options.</summary>
    internal static AutoTuneOptions FromPreset(AutoTunePreset preset) => preset switch
    {
        AutoTunePreset.Quick => Quick,
        AutoTunePreset.Thorough => Thorough,
        _ => Default,
    };
}
