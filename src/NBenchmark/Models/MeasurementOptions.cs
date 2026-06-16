using NBenchmark.Stats;

namespace NBenchmark;

public record MeasurementOptions
{
    public const int MinIterations = 0;
    public const int MaxIterations = 100_000;
    public const int MaxWarmupIterations = 10_000;
    public static readonly MeasurementOptions Default = new();
    private readonly double _confidenceLevel = 0.95;
    private readonly int _iterations = 200;
    private readonly double? _minimumPracticalEffect;
    private readonly double _significanceLevel = 0.05;
    private readonly int _warmupIterations = 25;

    public int WarmupIterations
    {
        get => _warmupIterations;
        init => _warmupIterations = value is >= 0 and <= MaxWarmupIterations
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"WarmupIterations must be between 0 and {MaxWarmupIterations}");
    }

    public int Iterations
    {
        get => _iterations;
        init => _iterations = value is >= 0 and <= MaxIterations
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Iterations must be between 0 and {MaxIterations}");
    }

    /// <summary>
    ///     The authoritative measurement profile. The resolved booleans
    ///     (<see cref="ForceGcBeforeEachIteration" />, <see cref="ForceGcBetweenBenchmarks" />,
    ///     <see cref="MeasureAllocations" />) derive from this unless an explicit override is set.
    /// </summary>
    public MeasurementProfile Profile { get; init; } = MeasurementProfile.Realistic;

    /// <summary>Overrides <see cref="ForceGcBeforeEachIteration" />. When <c>null</c>, the value derives from <see cref="Profile" />.</summary>
    public bool? ForceGcBeforeEachIterationOverride { get; init; }

    /// <summary>Overrides <see cref="ForceGcBetweenBenchmarks" />. When <c>null</c>, the value derives from <see cref="Profile" />.</summary>
    public bool? ForceGcBetweenBenchmarksOverride { get; init; }

    /// <summary>Overrides <see cref="MeasureAllocations" />. When <c>null</c>, the value derives from <see cref="Profile" />.</summary>
    public bool? MeasureAllocationsOverride { get; init; }

    /// <summary>Whether a Gen0 GC is forced before each measured iteration. Forced under <see cref="MeasurementProfile.Independent" />, unless overridden.</summary>
    public bool ForceGcBeforeEachIteration =>
        ForceGcBeforeEachIterationOverride ?? (Profile == MeasurementProfile.Independent);

    /// <summary>Whether a full GC runs between benchmarks. Forced under <see cref="MeasurementProfile.Independent" />, unless overridden.</summary>
    public bool ForceGcBetweenBenchmarks =>
        ForceGcBetweenBenchmarksOverride ?? (Profile == MeasurementProfile.Independent);

    /// <summary>Whether per-iteration allocations are sampled and reported. On under <see cref="MeasurementProfile.Realistic" />, unless overridden.</summary>
    public bool MeasureAllocations =>
        MeasureAllocationsOverride ?? (Profile == MeasurementProfile.Realistic);

    /// <summary>Creates options for the specified <paramref name="profile" />.</summary>
    public static MeasurementOptions For(MeasurementProfile profile) => new() { Profile = profile };

    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    /// <summary>
    ///     A custom outlier-detection strategy. When set, it takes precedence over
    ///     <see cref="OutlierMode" />, letting you plug in your own trimming algorithm.
    ///     Leave <c>null</c> to use the built-in detector selected by <see cref="OutlierMode" />.
    /// </summary>
    public IOutlierDetector? OutlierDetector { get; init; }

    /// <summary>
    ///     Confidence level for the interval reported on the mean (e.g. 0.95 for 95%).
    ///     Must be strictly between 0 and 1.
    /// </summary>
    public double ConfidenceLevel
    {
        get => _confidenceLevel;
        init => _confidenceLevel = value is > 0 and < 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "ConfidenceLevel must be strictly between 0 and 1 (e.g. 0.95).");
    }

    public bool EnableSignificance { get; init; } = true;

    /// <summary>
    ///     A custom statistical significance strategy. When set, it takes precedence over the
    ///     built-in default (Mann-Whitney U for two groups, Kruskal-Wallis for three or
    ///     more), letting you plug in your own comparison. Leave <c>null</c> to use the
    ///     default strategy.
    /// </summary>
    public ISignificanceTest? SignificanceTest { get; init; }

    /// <summary>
    ///     The significance level (alpha) a benchmark's p-value must fall below to be
    ///     reported as a statistically significant change versus the baseline. Must be
    ///     strictly between 0 and 1. Default 0.05. Tighten (e.g. 0.001) to gate releases
    ///     on a stricter confidence level.
    /// </summary>
    public double SignificanceLevel
    {
        get => _significanceLevel;
        init => _significanceLevel = value is > 0 and < 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "SignificanceLevel must be strictly between 0 and 1 (e.g. 0.05).");
    }

    /// <summary>
    ///     The minimum practical effect in [0, 1] required for a benchmark to be considered
    ///     meaningfully different. The active significance strategy can map its own effect
    ///     metric to this normalized value via <see cref="EffectSize.PracticalValue" />.
    ///     When set and the reported practical value is below this threshold, the Sig verdict
    ///     is downgraded to NotSignificant and the magnitude label is forced to <c>neg</c>.
    ///     Leave null to keep p-value-only Sig semantics.
    /// </summary>
    public double? MinimumPracticalEffect
    {
        get => _minimumPracticalEffect;
        init
        {
            if (!value.HasValue)
            {
                _minimumPracticalEffect = null;
                return;
            }

            var delta = value.Value;

            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0 || delta > 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value,
                    "MinimumPracticalEffect must be between 0 and 1 inclusive.");
            }

            _minimumPracticalEffect = delta;
        }
    }

    /// <summary>
    ///     Resolves the effective outlier detector: the custom
    ///     <see cref="OutlierDetector" /> when supplied, otherwise the built-in detector for
    ///     the configured <see cref="OutlierMode" />.
    /// </summary>
    public IOutlierDetector ResolveOutlierDetector() =>
        OutlierDetector ?? OutlierDetectors.ForMode(OutlierMode);

    /// <summary>
    ///     Resolves the effective significance test: the custom
    ///     <see cref="SignificanceTest" /> when supplied, otherwise
    ///     <see cref="DefaultSignificanceTest" />.
    /// </summary>
    public ISignificanceTest ResolveSignificanceTest() =>
        SignificanceTest ?? DefaultSignificanceTest.Instance;
}
