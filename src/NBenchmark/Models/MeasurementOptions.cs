namespace NBenchmark;

public record MeasurementOptions
{
    public static readonly MeasurementOptions Default = new();

    public const int MinIterations = 1;
    public const int MaxIterations = 100_000;
    public const int MaxWarmupIterations = 10_000;

    public int WarmupIterations
    {
        get => _warmupIterations;
        init => _warmupIterations = value is >= 1 and <= MaxWarmupIterations
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"WarmupIterations must be between 1 and {MaxWarmupIterations}");
    }
    private readonly int _warmupIterations = 25;

    public int Iterations
    {
        get => _iterations;
        init => _iterations = value is >= 1 and <= MaxIterations
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"Iterations must be between {MinIterations} and {MaxIterations}");
    }
    private readonly int _iterations = 200;

    public bool ForceGcBeforeEachIteration { get; init; } = true;

    public bool MeasureAllocations { get; init; } = false;

    public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;

    /// <summary>
    /// Confidence level for the interval reported on the mean (e.g. 0.95 for 95%).
    /// Must be strictly between 0 and 1.
    /// </summary>
    public double ConfidenceLevel
    {
        get => _confidenceLevel;
        init => _confidenceLevel = value is > 0 and < 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                "ConfidenceLevel must be strictly between 0 and 1 (e.g. 0.95).");
    }
    private readonly double _confidenceLevel = 0.95;

    public bool EnableSignificance { get; init; } = true;

    public bool ForceGcBetweenBenchmarks { get; init; } = true;
}
