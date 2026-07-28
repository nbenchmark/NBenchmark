namespace NBenchmark;

/// <summary>
///     Cross-launch summary statistics. Populated when
///     <see cref="MeasurementOptions.LaunchCount" /> > 1.
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

    /// <summary>
    ///     Run-to-run variation as a fraction of the typical measurement - the coefficient of
    ///     variation of the per-launch medians. This is the <em>reproducibility</em> of the number,
    ///     as opposed to the precision with which any one launch measured it.
    ///     <para>
    ///         <c>null</c> when fewer than two launches succeeded, because a single process cannot
    ///         say anything about run-to-run behaviour.
    ///     </para>
    /// </summary>
    public double? BetweenLaunchDispersion { get; init; }

    /// <summary>
    ///     How much larger the spread <em>between</em> processes is than the spread <em>within</em>
    ///     one. Near 1 means the within-process confidence interval fairly describes what a re-run
    ///     would produce; a large value means it does not.
    ///     <para>
    ///         This exposes the most dangerous failure mode in benchmarking: a tight interval around
    ///         a value that does not reproduce. On this library's own sample, an in-process
    ///         measurement reported a standard deviation of 0.16 ns on an 11 ns reading while the
    ///         true run-to-run spread was 3.27x. Statistics computed from pooled samples inherit the
    ///         power of the pooled count, so at a ratio like that a p-value can be arbitrarily small
    ///         and still mean nothing.
    ///     </para>
    /// </summary>
    public double? ProcessVarianceRatio { get; init; }

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
