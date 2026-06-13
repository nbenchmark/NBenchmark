namespace NBenchmark;

/// <summary>
///     The outcome of an <i>omnibus</i> significance test - one that asks a single question
///     across all benchmarks at once ("is at least one of these groups different?") rather
///     than comparing each candidate to the baseline individually. Produced by the
///     Kruskal-Wallis test when three or more benchmarks are compared.
/// </summary>
public sealed record OmnibusComparison
{
    /// <summary>The display name of the test that produced this result (e.g. <c>"Kruskal-Wallis"</c>).</summary>
    public required string TestName { get; init; }

    /// <summary>The test statistic (e.g. the Kruskal-Wallis <c>H</c>).</summary>
    public required double Statistic { get; init; }

    /// <summary>The p-value for the omnibus null hypothesis that all groups share one distribution.</summary>
    public required double PValue { get; init; }

    /// <summary>Degrees of freedom associated with the statistic (<c>k − 1</c> for Kruskal-Wallis).</summary>
    public required int DegreesOfFreedom { get; init; }

    /// <summary>The number of groups (benchmarks) included in the test.</summary>
    public required int GroupCount { get; init; }

    /// <summary>Whether the p-value cleared the configured significance level.</summary>
    public required SignificanceVerdict Verdict { get; init; }
}
