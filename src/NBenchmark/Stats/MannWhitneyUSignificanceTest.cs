namespace NBenchmark.Stats;

/// <summary>
///     A two-sample significance strategy: compares each candidate to the baseline with the
///     <see cref="MannWhitneyU" /> U test and reports one verdict per candidate. Best suited
///     to comparing exactly two benchmarks; for three or more, prefer the omnibus
///     <see cref="KruskalWallisSignificanceTest" /> (the default engine behavior) to avoid
///     inflating the false-positive rate across many pairwise comparisons.
/// </summary>
public sealed class MannWhitneyUSignificanceTest : ISignificanceTest
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly MannWhitneyUSignificanceTest Instance = new();

    /// <inheritdoc />
    public string Name => "Mann-Whitney U";

    /// <inheritdoc />
    public SignificanceReport Analyze(SignificanceContext context)
    {
        var baseline = context.Baseline;
        var pairwise = new List<PairwiseComparison>();

        foreach (var candidate in context.Candidates)
        {
            var pValue = MannWhitneyU.Test(baseline.Samples, candidate.Samples);

            if (double.IsNaN(pValue))
            {
                pairwise.Add(new PairwiseComparison(candidate.Name, null, SignificanceVerdict.NotTested));
                continue;
            }

            var verdict = pValue < context.SignificanceLevel
                ? SignificanceVerdict.Significant
                : SignificanceVerdict.NotSignificant;

            pairwise.Add(new PairwiseComparison(candidate.Name, pValue, verdict));
        }

        return new SignificanceReport { Pairwise = pairwise };
    }
}
