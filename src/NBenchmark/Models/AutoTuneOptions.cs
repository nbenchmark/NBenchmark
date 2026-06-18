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
    };

    /// <summary>More samples and a tighter CI target for publication-grade numbers.</summary>
    public static readonly AutoTuneOptions Thorough = new()
    {
        MinWarmup = 16,
        MinSamples = 100,
        CiTarget = 0.01,
        TargetSampleDurationNs = 4_000,
        MaxTuningTime = TimeSpan.FromSeconds(60),
    };

    private readonly int _batchSize = 8;
    private readonly int _maxOpsPerSample = 1 << 20;
    private readonly int _plateauPatience = 3;

    // ----- Warmup plateau -----

    /// <summary>The earliest sample at which auto-warmup may settle. Default 8.</summary>
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
    ///     timer overhead. Default 1,000 (1 µs).
    /// </summary>
    public double TargetSampleDurationNs { get; init; } = 1_000;

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

    /// <summary>
    ///     What happens when the adaptive loop stops because it hit the wall-clock tuning cap
    ///     before reaching the confidence-interval target or a steady warmup state. Default
    ///     <see cref="AutoTuneCapBehavior.Warn" />.
    /// </summary>
    public AutoTuneCapBehavior CapBehavior { get; init; } = AutoTuneCapBehavior.Warn;

    /// <summary>Resolves a <see cref="AutoTunePreset" /> to its concrete options.</summary>
    public static AutoTuneOptions FromPreset(AutoTunePreset preset) => preset switch
    {
        AutoTunePreset.Quick => Quick,
        AutoTunePreset.Thorough => Thorough,
        _ => Default,
    };
}
