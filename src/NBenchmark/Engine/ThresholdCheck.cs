namespace NBenchmark.Engine;

/// <summary>
///     Threshold-based regression detection: compares each successful benchmark's median
///     against a baseline (the <see cref="BenchmarkResult.IsBaseline" /> flag, or the
///     fastest by median when no baseline is declared) and flags any candidate whose median
///     exceeds the baseline by more than a configured percentage. Public so an external
///     consumer (for example NBenchmark.Studio) can reuse the same baseline-selection and
///     ratio-gate logic that the engine uses for its <c>--threshold-pct</c> exit-code gate,
///     instead of reimplementing a weaker version.
/// </summary>
public static class ThresholdCheck
{
    /// <summary>
    ///     Returns the regressed benchmark names when one or more candidates exceed the
    ///     baseline by more than <paramref name="thresholdPct" /> percent; otherwise
    ///     <c>(false, [])</c>. Convenience overload for callers that only need the names.
    /// </summary>
    public static (bool HasRegression, IReadOnlyList<string> RegressedNames) HasRegression(
        IReadOnlyList<BenchmarkResult> results, int thresholdPct)
    {
        if (thresholdPct <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdPct), "Must be a positive integer (1 or greater).");

        var verdict = Check(results, thresholdPct);
        return (verdict.HasRegression, verdict.RegressedNames);
    }

    /// <summary>
    ///     Returns a <see cref="RegressionVerdict" /> carrying the baseline, the regressed
    ///     candidates with their ratio and delta against the baseline, and the sorted
    ///     regressed names. Use this overload when the caller needs the structured per-benchmark
    ///     values (for example to build a regression-alert UI) rather than just the names.
    /// </summary>
    public static RegressionVerdict Check(IReadOnlyList<BenchmarkResult> results, int thresholdPct)
    {
        if (thresholdPct <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdPct), "Must be a positive integer (1 or greater).");

        var successful = results.Where(r => !r.Errored).ToList();

        if (successful.Count <= 1)
            return RegressionVerdict.None;

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                   ?? successful.MinBy(ComparisonMedian)!;

        var baselineMedian = ComparisonMedian(baseline);

        var regressedCandidates = new List<RegressionCandidate>();
        var regressedNames = new List<string>();

        if (baselineMedian <= 0)
        {
            // Ratio comparison is undefined at zero; treat any positive candidate median as slower.
            for (var i = 0; i < successful.Count; i++)
            {
                var result = successful[i];
                var candidateMedian = ComparisonMedian(result);

                if (ReferenceEquals(result, baseline))
                    continue;

                if (candidateMedian > 0)
                {
                    regressedCandidates.Add(new RegressionCandidate(result.Name, candidateMedian, baselineMedian, double.NaN, candidateMedian));
                    regressedNames.Add(result.Name);
                }
            }
        }
        else
        {
            var thresholdMedian = baselineMedian * (1.0 + thresholdPct / 100.0);

            for (var i = 0; i < successful.Count; i++)
            {
                var result = successful[i];
                var candidateMedian = ComparisonMedian(result);

                if (ReferenceEquals(result, baseline))
                    continue;

                if (candidateMedian > thresholdMedian)
                {
                    regressedCandidates.Add(new RegressionCandidate(result.Name, candidateMedian, baselineMedian, candidateMedian / baselineMedian, candidateMedian - baselineMedian));
                    regressedNames.Add(result.Name);
                }
            }
        }

        if (regressedCandidates.Count == 0)
            return RegressionVerdict.None;

        regressedNames.Sort(StringComparer.Ordinal);

        return new RegressionVerdict(
            HasRegression: true,
            baseline.Name,
            regressedCandidates,
            regressedNames);
    }

    private static double ComparisonMedian(BenchmarkResult result)
        => result.LaunchStatistics?.LaunchMedian ?? result.Median;
}

/// <summary>
///     A structured regression-detection result: the baseline the comparison was made
///     against, the candidates that exceeded the threshold (with their ratio and delta),
///     and the regressed names sorted ascending. Returned by
///     <see cref="ThresholdCheck.Check" />.
/// </summary>
/// <param name="HasRegression"><c>true</c> when at least one candidate exceeded the threshold.</param>
/// <param name="BaselineName">The name of the benchmark the comparison was made against.</param>
/// <param name="RegressedCandidates">One entry per regressed candidate, in evaluation order.</param>
/// <param name="RegressedNames">The regressed candidate names, sorted ascending by ordinal.</param>
public sealed record RegressionVerdict(
    bool HasRegression,
    string BaselineName,
    IReadOnlyList<RegressionCandidate> RegressedCandidates,
    IReadOnlyList<string> RegressedNames)
{
    internal static RegressionVerdict None { get; } = new(false, "", [], []);
}

/// <summary>
///     One regressed candidate: its median, the baseline median, the ratio
///     (<c>candidate / baseline</c>, <c>NaN</c> when the baseline median is zero), and the
///     absolute delta in nanoseconds. Built by <see cref="ThresholdCheck.Check" />.
/// </summary>
public sealed record RegressionCandidate(string Name, double CandidateMedian, double BaselineMedian, double Ratio, double DeltaNs);
