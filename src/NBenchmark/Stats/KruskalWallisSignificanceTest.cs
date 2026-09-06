namespace NBenchmark.Stats;

/// <summary>
///     An omnibus significance strategy: runs a single <see cref="KruskalWallis" /> H test
///     across all groups and reports one verdict for the whole comparison ("is at least one
///     of these benchmarks different?"). It is the omnibus stage used by
///     <see cref="DefaultSignificanceTest" /> when three or more benchmarks are compared.
/// </summary>
public sealed class KruskalWallisSignificanceTest : ISignificanceTest
{
    /// <summary>A shared, stateless instance.</summary>
    public static readonly KruskalWallisSignificanceTest Instance = new();

    /// <inheritdoc />
    public string Name => "Kruskal-Wallis";

    /// <inheritdoc />
    public SignificanceReport Analyze(SignificanceContext context)
    {
        var groups = context.Groups;
        var samples = new ReadOnlyMemory<double>[groups.Count];

        for (var i = 0; i < groups.Count; i++)
        {
            samples[i] = groups[i].Samples;
        }

        var result = KruskalWallis.Test(samples);

        if (!result.IsValid)
            return SignificanceReport.Empty;

        var verdict = result.PValue < context.SignificanceLevel
            ? SignificanceVerdict.Significant
            : SignificanceVerdict.NotSignificant;

        var omnibus = new OmnibusComparison
        {
            TestName = Name,
            Statistic = result.H,
            PValue = result.PValue,
            DegreesOfFreedom = result.DegreesOfFreedom,
            GroupCount = result.GroupCount,
            Verdict = verdict,
        };

        return new SignificanceReport { Pairwise = [], Omnibus = omnibus };
    }
}
