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
///             three or more groups - the omnibus <see cref="KruskalWallisSignificanceTest" />,
///             producing a single verdict across all benchmarks. A single omnibus test is
///             used instead of many pairwise comparisons to avoid inflating the
///             false-positive rate.
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
    ///     The pairwise test name. The two-group path uses Mann-Whitney U; the omnibus path
    ///     describes itself through <see cref="OmnibusComparison.TestName" />.
    /// </summary>
    public string Name => "Mann-Whitney U";

    /// <inheritdoc />
    public SignificanceReport Analyze(SignificanceContext context)
    {
        // Three or more groups (at least two candidates against the baseline) call for a
        // single omnibus test; two groups use the pairwise two-sample test.
        var candidateCount = context.Groups.Count - 1;

        return candidateCount >= 2
            ? KruskalWallisSignificanceTest.Instance.Analyze(context)
            : MannWhitneyUSignificanceTest.Instance.Analyze(context);
    }
}
