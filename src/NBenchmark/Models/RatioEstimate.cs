namespace NBenchmark;

/// <summary>
///     A ratio between two benchmarks with an interval on it, estimated by pairing their replicates.
/// </summary>
/// <remarks>
///     <para>
///         The plain ratio column divides two aggregated medians and can say nothing about how much
///         that number would move on a re-run. This can, because it is built from the per-replicate
///         ratios rather than from the ratio of the aggregates - and because a comparison group is
///         measured co-resident in one worker per replicate, each of those per-replicate ratios has
///         that worker's own CPU draw, thermal state and memory layout divided out of it.
///     </para>
///     <para>
///         Read <see cref="Lower" /> and <see cref="Upper" /> as the range the ratio would plausibly
///         land in if the whole run were repeated. An interval spanning 1.0 means the two benchmarks
///         are not distinguishable at this replicate count, however far <see cref="Value" /> is from
///         1.0 - which is the question a reader is actually asking when they look at a ratio, and the
///         one a bare number cannot answer.
///     </para>
/// </remarks>
public sealed record RatioEstimate
{
    /// <summary>
    ///     The estimated ratio: the <b>geometric</b> mean of the per-replicate ratios. Above 1.0 means
    ///     slower than the reference.
    /// </summary>
    /// <remarks>
    ///     Geometric rather than arithmetic because a ratio is multiplicative - the mean of 0.5x and
    ///     2.0x is 1.0x, not 1.25x. It is also not the ratio of the two aggregated medians, and where
    ///     the two disagree this one is the estimate that accounts for the pairing.
    /// </remarks>
    public required double Value { get; init; }

    /// <summary>Lower bound of the interval on <see cref="Value" />.</summary>
    public required double Lower { get; init; }

    /// <summary>Upper bound of the interval on <see cref="Value" />.</summary>
    public required double Upper { get; init; }

    /// <summary>How many replicates were paired to produce this. Always at least two.</summary>
    public required int Replicates { get; init; }

    /// <summary>The confidence level <see cref="Lower" /> and <see cref="Upper" /> were computed at.</summary>
    public required double ConfidenceLevel { get; init; }

    /// <summary>
    ///     Whether the interval contains 1.0 - i.e. whether "no difference" is among the values this
    ///     run cannot rule out.
    /// </summary>
    public bool IncludesUnity => Lower <= 1.0 && Upper >= 1.0;

    /// <summary>
    ///     The interval rendered as a multiplicative range, e.g. <c>"1.08-1.31x"</c>. Empty when the
    ///     bounds are not finite.
    /// </summary>
    public string FormatInterval()
        => double.IsFinite(Lower) && double.IsFinite(Upper)
            ? $"{Lower:0.00}-{Upper:0.00}x"
            : "";
}
