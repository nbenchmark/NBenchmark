namespace NBenchmark;

public record MeasurementOptions
{
    public const int MinIterations = 0;
    public const int MaxIterations = 100_000;
    public const int MaxWarmupIterations = 10_000;
    public static readonly MeasurementOptions Default = new();
    private readonly double _confidenceLevel = 0.95;
    private readonly int _iterations = 200;
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

    public bool ForceGcBeforeEachIteration { get; init; } = true;

    public bool MeasureAllocations { get; init; } = false;

    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

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

    public bool ForceGcBetweenBenchmarks { get; init; } = true;
}
