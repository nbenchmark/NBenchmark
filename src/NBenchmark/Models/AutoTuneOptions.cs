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

    /// <summary>Fewer samples and a looser CI target for fast feedback.</summary>
    public static readonly AutoTuneOptions Quick = new()
    {
        MinWarmup = 4,
        MinSamples = 15,
        CiTarget = 0.05,
        MaxTuningTime = TimeSpan.FromSeconds(5),
        BatchSize = 4,
        PlateauPatience = 2,
        MinWarmupTime = TimeSpan.FromMilliseconds(25),
    };

    /// <summary>More samples and a tighter CI target for publication-grade numbers.</summary>
    public static readonly AutoTuneOptions Thorough = new()
    {
        MinWarmup = 16,
        MinSamples = 100,
        CiTarget = 0.01,
        TargetSampleDurationNs = 50_000,
        MaxTuningTime = TimeSpan.FromSeconds(60),
    };

    private readonly int _batchSize = 8;
    private readonly int _maxOpsPerSample = 1 << 20;
    private readonly int _plateauPatience = 3;
    private readonly double _warmupBudgetFraction = 0.4;
    private readonly double _capGraceFactor = 1.5;
    private readonly TimeSpan _minWarmupTime = TimeSpan.FromMilliseconds(100);

    // ----- Warmup plateau -----

    /// <summary>
    ///     The earliest sample at which auto-warmup may settle. Default 8.
    ///     <para>
    ///         This is a floor on the sample <em>count</em>. Note the plateau rule cannot settle
    ///         before it has seen <c>(PlateauPatience + 1) × BatchSize</c> samples (one improving
    ///         batch plus <see cref="PlateauPatience" /> non-improving ones), so with the defaults
    ///         (patience 3, batch 8) the effective minimum is 32 samples and <c>MinWarmup</c> only
    ///         binds when raised above that. <see cref="MinWarmupTime" /> is the independent floor
    ///         on warmup <em>wall-clock</em>, which for fast bodies is usually the binding one.
    ///     </para>
    /// </summary>
    public int MinWarmup { get; init; } = 8;

    /// <summary>The warmup ceiling. Default 10,000 (== <see cref="MeasurementOptions.MaxWarmupIterations" />).</summary>
    public int MaxWarmup { get; init; } = 10_000;

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
    ///     The minimum wall-clock time auto-warmup must run before it may settle, regardless of how
    ///     quickly the plateau rule is satisfied. Default 100 ms (25 ms under the <c>Quick</c> preset).
    ///     <para>
    ///         The plateau rule measures warmup in <em>iterations</em>, but a fast body plateaus in
    ///         microseconds of wall-clock - long before the background JIT delivers tier-1 (and
    ///         dynamic-PGO) code. Warmup then settles on the stable but slow tier-0 plateau and the
    ///         tier-1 switch lands mid-measurement as a step change, the dominant source of
    ///         run-to-run variance on very fast benchmarks. This floor holds warmup open long enough
    ///         for tiered compilation to land. It is bounded above by the calibration+warmup budget
    ///         share (<see cref="WarmupBudgetFraction" /> of <see cref="MaxTuningTime" />) and by
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
    ///     Whether auto-warmup additionally refuses to settle while the JIT is still compiling
    ///     methods (a proxy for in-flight tier-1 promotion of the body under test). Default <c>true</c>.
    ///     <para>
    ///         At each warmup batch boundary the loop reads <see cref="System.Runtime.JitInfo" />'s
    ///         compiled-method count; while the count is still rising over a batch, warmup continues.
    ///         To avoid blocking forever on a busy in-process host that JITs unrelated code, the gate
    ///         deactivates once warmup has run for 4 × <see cref="MinWarmupTime" />. The gate is
    ///         inactive when <see cref="MinWarmupTime" /> is <see cref="TimeSpan.Zero" />. Set to
    ///         <c>false</c> to keep only the time floor.
    ///     </para>
    /// </summary>
    public bool RequireJitQuiescence { get; init; } = true;

    // ----- CI-width sample count -----

    /// <summary>The earliest sample at which auto-measurement may stop on the CI target. Default 30.</summary>
    public int MinSamples { get; init; } = 30;

    /// <summary>The measured-sample ceiling. Default 100,000 (== <see cref="MeasurementOptions.MaxIterations" />).</summary>
    public int MaxSamples { get; init; } = 100_000;

    /// <summary>
    ///     The target relative half-width of the confidence interval on the mean. Measurement
    ///     stops once the achieved half-width divided by the mean falls below this. Default 0.025
    ///     (±2.5%).
    /// </summary>
    public double CiTarget { get; init; } = 0.025;

    // ----- Ops-per-sample calibration -----

    /// <summary>
    ///     The target duration of a single timed sample, in nanoseconds. Auto-calibration doubles
    ///     the ops-per-sample count until a sample spans at least this long, amortising fixed
    ///     timer overhead. Default 10,000 (10 µs).
    ///     <para>
    ///         10 µs keeps two per-sample error sources negligible: timer <em>quantization</em>
    ///         (Windows QPC ticks at 100 ns, so a 10 µs sample resolves to ~0.1% rather than the
    ///         ~±10% a 1 µs sample suffers) and the fixed <em>timestamp-read overhead</em> (~10-30 ns
    ///         per sample, ~0.2% of 10 µs rather than ~1-3% of 1 µs). Both would otherwise leak into
    ///         the ±2.5% CI target the calibration feeds. Bodies already spanning ≥ 10 µs keep
    ///         <c>K = 1</c>, so their per-op tail visibility is unchanged; only sub-10 µs bodies are
    ///         batched (and for those, percentiles describe batch means - see the docs).
    ///     </para>
    /// </summary>
    public double TargetSampleDurationNs { get; init; } = 10_000;

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
    public static AutoTuneOptions FromPreset(AutoTunePreset preset) => preset switch
    {
        AutoTunePreset.Quick => Quick,
        AutoTunePreset.Thorough => Thorough,
        _ => Default,
    };
}
