namespace NBenchmark.Stats;

/// <summary>
///     The engine's default significance strategy. It selects the appropriate built-in test
///     by group count:
///     <list type="bullet">
///         <item>
///             two groups (one candidate versus the baseline) - the two-sample
///             <see cref="MannWhitneyUSignificanceTest" />, producing a pairwise verdict;
///         </item>
///         <item>
///             three or more groups - the omnibus <see cref="KruskalWallisSignificanceTest" />
///             first; if the omnibus is significant, a post-hoc pairwise Mann-Whitney U
///             (candidate versus baseline) with Holm-Bonferroni correction over the tested
///             candidates follows, so each benchmark gets a per-row verdict.
///         </item>
///     </list>
///     Override it with <see cref="MeasurementOptions.SignificanceTest" /> or
///     <c>BenchmarkSuite.WithSignificanceTest(...)</c> to force a specific strategy.
/// </summary>
public sealed class DefaultSignificanceTest : ISignificanceTest
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly DefaultSignificanceTest Instance = new();

    /// <summary>
    ///     The label used when no omnibus verdict overrides it in reporters (for example,
    ///     the two-group path).
    /// </summary>
    public string Name => "Mann-Whitney U";

    /// <inheritdoc />
    public SignificanceReport Analyze(SignificanceContext context)
    {
        var candidateCount = context.Groups.Count - 1;

        // Two groups (one candidate against the baseline): pairwise Mann-Whitney U.
        if (candidateCount <= 1)
            return MannWhitneyUSignificanceTest.Instance.Analyze(context);

        // Three or more groups: run the Kruskal-Wallis omnibus first.
        var omnibusReport = KruskalWallisSignificanceTest.Instance.Analyze(context);

        // If the omnibus is not significant, skip post-hoc: no group differs from the rest.
        if (omnibusReport.Omnibus is not { } omnibus
            || omnibus.Verdict != SignificanceVerdict.Significant)
        {
            return omnibusReport;
        }

        // Omnibus is significant: run pairwise Mann-Whitney U (candidate vs baseline)
        // and apply Holm-Bonferroni over the tested candidates.
        var baseline = context.Baseline;
        var rawPValues = new List<double>(candidateCount);
        var order = new List<string>(candidateCount);

        foreach (var candidate in context.Candidates)
        {
            rawPValues.Add(MannWhitneyU.Test(baseline.Samples, candidate.Samples));
            order.Add(candidate.Name);
        }

        var adjusted = MultipleComparisons.HolmBonferroni(rawPValues);
        var pairwise = new List<PairwiseComparison>(candidateCount);

        for (var i = 0; i < order.Count; i++)
        {
            var p = rawPValues[i];

            if (double.IsNaN(p))
            {
                pairwise.Add(new PairwiseComparison(order[i], null, SignificanceVerdict.NotTested));
                continue;
            }

            var verdict = adjusted[i] < context.SignificanceLevel
                ? SignificanceVerdict.Significant
                : SignificanceVerdict.NotSignificant;

            pairwise.Add(new PairwiseComparison(order[i], p, verdict));
        }

        return new SignificanceReport { Pairwise = pairwise, Omnibus = omnibus };
    }
}
