namespace NBenchmark.Stats;

/// <summary>
///     A pluggable statistical significance strategy. Implement this interface to control
///     how the engine decides whether benchmarks differ - for example to swap in a custom
///     bootstrap or Bayesian comparison, or a post-hoc procedure tuned to your latency
///     distribution. Register it with <see cref="MeasurementOptions.SignificanceTest" />,
///     <c>BenchmarkSuite.WithSignificanceTest(...)</c>, or <c>BenchmarkHost.WithOptions(...)</c>.
///     <para>
///         The default strategy (<see cref="DefaultSignificanceTest" />) selects the
///         appropriate built-in test by group count: the two-sample
///         <see cref="MannWhitneyUSignificanceTest" /> for a single candidate versus the
///         baseline, and for three or more groups an omnibus
///         <see cref="KruskalWallisSignificanceTest" /> gate followed by post-hoc
///         pairwise Mann-Whitney U with Holm-Bonferroni correction.
///     </para>
/// </summary>
public interface ISignificanceTest
{
    /// <summary>A short, human-readable label shown in reports (e.g. <c>"Mann-Whitney U"</c>).</summary>
    string Name { get; }

    /// <summary>
    ///     Analyzes the pre-trim raw samples in <paramref name="context" /> and returns the
    ///     pairwise verdicts (candidate versus baseline) and/or an omnibus verdict across all
    ///     groups.
    /// </summary>
    SignificanceReport Analyze(SignificanceContext context);
}

/// <summary>A named set of raw measurements for one benchmark.</summary>
public readonly record struct SampleGroup(string Name, double[] Samples, bool IsBaseline);

/// <summary>
///     The input to <see cref="ISignificanceTest.Analyze" />: all comparable groups, the
///     index of the baseline within them, and the significance level (alpha) a p-value must
///     fall below to count as significant.
/// </summary>
public sealed record SignificanceContext
{
    /// <summary>All groups participating in the comparison, including the baseline.</summary>
    public required IReadOnlyList<SampleGroup> Groups { get; init; }

    /// <summary>The index of the baseline group within <see cref="Groups" />.</summary>
    public required int BaselineIndex { get; init; }

    /// <summary>The significance level (alpha), e.g. 0.05.</summary>
    public required double SignificanceLevel { get; init; }

    /// <summary>The baseline group.</summary>
    public SampleGroup Baseline => Groups[BaselineIndex];

    /// <summary>Every group except the baseline.</summary>
    public IEnumerable<SampleGroup> Candidates
    {
        get
        {
            for (var i = 0; i < Groups.Count; i++)
            {
                if (i != BaselineIndex)
                    yield return Groups[i];
            }
        }
    }
}

/// <summary>
///     The output of <see cref="ISignificanceTest.Analyze" />: zero or more pairwise verdicts
///     and an optional omnibus verdict.
/// </summary>
public sealed record SignificanceReport
{
    /// <summary>An empty report (no verdicts).</summary>
    public static readonly SignificanceReport Empty = new() { Pairwise = [] };

    /// <summary>Per-candidate verdicts versus the baseline, keyed by benchmark name.</summary>
    public required IReadOnlyList<PairwiseComparison> Pairwise { get; init; }

    /// <summary>An omnibus verdict across all groups, when the test produced one; otherwise <c>null</c>.</summary>
    public OmnibusComparison? Omnibus { get; init; }
}

/// <summary>A single candidate-versus-baseline significance verdict.</summary>
public readonly record struct PairwiseComparison(string Name, double? PValue, SignificanceVerdict Verdict);
