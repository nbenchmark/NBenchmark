namespace NBenchmark;

/// <summary>
///     Cross-launch summary statistics. Populated when
///     <see cref="MeasurementOptions.LaunchCount"/> > 1.
/// </summary>
public sealed record LaunchStatistics
{
    /// <summary>The number of successful launches aggregated.</summary>
    public required int LaunchCount { get; init; }

    /// <summary>Arithmetic mean of per-launch medians (ns).</summary>
    public required double LaunchMean { get; init; }

    /// <summary>Sample standard deviation of per-launch medians (ns).</summary>
    public required double LaunchStandardDeviation { get; init; }

    /// <summary>Median of per-launch medians (ns).</summary>
    public required double LaunchMedian { get; init; }

    /// <summary>Lower bound of the confidence interval on the launch mean.</summary>
    public double? LaunchConfidenceIntervalLower { get; init; }

    /// <summary>Upper bound of the confidence interval on the launch mean.</summary>
    public double? LaunchConfidenceIntervalUpper { get; init; }

    /// <summary>Per-launch detail records.</summary>
    public IReadOnlyList<LaunchDetail> Launches { get; init; } = [];
}

/// <summary>Summary of a single launch.</summary>
public sealed record LaunchDetail
{
    /// <summary>Zero-based launch index.</summary>
    public required int LaunchIndex { get; init; }

    /// <summary>Median latency for this launch (ns).</summary>
    public required double Median { get; init; }

    /// <summary>Mean latency for this launch (ns).</summary>
    public required double Mean { get; init; }

    /// <summary>Standard deviation for this launch (ns).</summary>
    public required double StandardDeviation { get; init; }

    /// <summary>Measured iterations in this launch.</summary>
    public required int Iterations { get; init; }

    /// <summary>Wall-clock duration of this launch.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>True when this launch errored.</summary>
    public bool Errored { get; init; }

    /// <summary>Error message when this launch errored.</summary>
    public string? ErrorMessage { get; init; }
}
