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
            var result = MannWhitneyU.Test(baseline.Samples, candidate.Samples);

            if (double.IsNaN(result.PValue))
            {
                pairwise.Add(new PairwiseComparison(candidate.Name, null, SignificanceVerdict.NotTested));
                continue;
            }

            var verdict = result.PValue < context.SignificanceLevel
                ? SignificanceVerdict.Significant
                : SignificanceVerdict.NotSignificant;

            EffectSize? effect = null;

            if (!double.IsNaN(result.CliffsDelta))
                effect = EffectSizeFactory.ForCliffsDelta(result.CliffsDelta);

            pairwise.Add(new PairwiseComparison(candidate.Name, result.PValue, verdict, effect));
        }

        return new SignificanceReport { Pairwise = pairwise };
    }
}
