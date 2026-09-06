namespace NBenchmark;

/// <summary>
///     Cross-launch summary statistics. Populated when the launch count (see
///     <see cref="LaunchCounts" />) is above one.
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
    ///     How much larger the spread <em>between</em> processes is than the precision a single process
    ///     claimed about its own median: <see cref="LaunchStandardDeviation" /> divided by
    ///     <see cref="WithinLaunchStandardError" />. Near 1 means a within-process interval fairly
    ///     describes what a re-run would produce; a large value means it does not.
    ///     <para>
    ///         This exposes the most dangerous failure mode in benchmarking: a tight interval around a
    ///         value that does not reproduce. Statistics computed from pooled samples inherit the power
    ///         of the pooled count, so at a large ratio a p-value can be arbitrarily small and still
    ///         mean nothing.
    ///     </para>
    ///     <para>
    ///         The denominator is the standard <em>error</em>, not the standard deviation of individual
    ///         samples. A within-process interval is <c>t * s / sqrt(n)</c>, so comparing between-process
    ///         spread against <c>s</c> understates the problem by <c>sqrt(n)</c> - and <c>n</c> reaches
    ///         the thousands on precisely the cheap bodies where this matters. Measured on this library's
    ///         own calibration sample, a single-launch interval 21x narrower than the true run-to-run
    ///         spread produced a ratio of 0.7 against a threshold of 4.
    ///     </para>
    ///     <para>
    ///         Expect large values on nanosecond-scale bodies. A ratio of 30-50 there is ordinary and
    ///         reflects real machine variance - code and heap layout, scheduler placement, clock
    ///         granularity - not a defect in the benchmark. It is a statement about which interval to
    ///         trust, not a failure.
    ///     </para>
    ///     <para>
    ///         <c>null</c> when fewer than two launches succeeded, or when every launch reported a zero
    ///         standard error.
    ///     </para>
    /// </summary>
    public double? ProcessVarianceRatio { get; init; }

    /// <summary>
    ///     The mean of the per-launch standard errors (ns) - the typical precision one launch claimed
    ///     about its own median, and the denominator of <see cref="ProcessVarianceRatio" />. Exposed so
    ///     the ratio can be audited rather than taken on trust.
    ///     <para>
    ///         Read against <see cref="LaunchStandardDeviation" />: the first is how precisely a single
    ///         process measured, the second is how much the answer moves when you run it again.
    ///     </para>
    ///     <para>
    ///         <c>null</c> under the same conditions as <see cref="ProcessVarianceRatio" />.
    ///     </para>
    /// </summary>
    public double? WithinLaunchStandardError { get; init; }

    /// <summary>Per-launch detail records.</summary>
    public IReadOnlyList<LaunchDetail> Launches { get; init; } = [];
}

/// <summary>Summary of a single launch.</summary>
public sealed record LaunchDetail
{
    /// <summary>Zero-based launch index.</summary>
    public required int LaunchIndex { get; init; }

    /// <summary>Median latency for this launch (ns).</summary>
    public required double MedianNs { get; init; }

    /// <summary>Mean latency for this launch (ns).</summary>
    public required double MeanNs { get; init; }

    /// <summary>Standard deviation for this launch (ns).</summary>
    public required double StandardDeviationNs { get; init; }

    /// <summary>Measured samples in this launch.</summary>
    public required int Samples { get; init; }

    /// <summary>Wall-clock duration of this launch.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>True when this launch errored.</summary>
    public bool Errored { get; init; }

    /// <summary>Error message when this launch errored.</summary>
    public string? ErrorMessage { get; init; }
}
