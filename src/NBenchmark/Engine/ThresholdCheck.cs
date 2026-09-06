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
internal static class ThresholdCheck
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
    ///     Returns the regressed benchmark names across a run that may mix comparison groups
    ///     and classes, evaluating the gate once per <see cref="ComparisonGroup" /> partition
    ///     <i>and</i> per benchmark class. A candidate is only ever flagged against a baseline
    ///     measured under the same runtime and runtime profile in the same class.
    ///     <para>
    ///         This is the entry point the engine's <c>--threshold-pct</c> exit-code gate uses,
    ///         because a run's <c>allResults</c> is the union of every class and (under
    ///         <c>--runtime-profile host</c> or a mixed isolated/in-process run) every
    ///         configuration. Feeding that list to <see cref="HasRegression" /> directly picks
    ///         the fastest row overall as the implicit baseline: an in-process row is typically
    ///         ~3.3x faster than an isolated one purely from configuration, and an unrelated
    ///         benchmark in a different class is not this class's reference, so both fabricate
    ///         regressions. Partitioning prevents either comparison from being formed.
    ///     </para>
    /// </summary>
    public static (bool HasRegression, IReadOnlyList<string> RegressedNames) HasRegressionAcrossGroups(
        IReadOnlyList<BenchmarkResult> results, int thresholdPct)
    {
        if (thresholdPct <= 0)
            throw new ArgumentOutOfRangeException(nameof(thresholdPct), "Must be a positive integer (1 or greater).");

        var regressed = new List<string>();
        foreach (var partition in results.GroupBy(PartitionKey))
        {
            if (Check(partition.ToList(), thresholdPct) is { HasRegression: true } verdict)
                regressed.AddRange(verdict.RegressedNames);
        }

        regressed.Sort(StringComparer.Ordinal);
        return (regressed.Count > 0, regressed);
    }

    /// <summary>
    ///     The partition a result belongs to for the threshold gate: its comparison group
    ///     (runtime moniker + runtime profile, via <see cref="ComparisonGroup.KeyFor" />) and
    ///     its declaring class. Results with equal partition keys are comparable; results that
    ///     differ on either dimension are never compared.
    /// </summary>
    private static ((string RuntimeMoniker, string RuntimeProfileName, bool Isolated), string ClassName) PartitionKey(
        BenchmarkResult result)
        => (ComparisonGroup.KeyFor(result), result.ClassName);

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
                       ?? successful.MinBy(ComparisonGroup.ComparisonMedian)!;

        var baselineMedian = ComparisonGroup.ComparisonMedian(baseline);

        var regressedCandidates = new List<RegressionCandidate>();
        var regressedNames = new List<string>();

        if (baselineMedian <= 0)
        {
            // Ratio comparison is undefined at zero; treat any positive candidate median as slower.
            for (var i = 0; i < successful.Count; i++)
            {
                var result = successful[i];
                var candidateMedian = ComparisonGroup.ComparisonMedian(result);

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
            var threshold = 1.0 + thresholdPct / 100.0;

            for (var i = 0; i < successful.Count; i++)
            {
                var result = successful[i];
                var candidateMedian = ComparisonGroup.ComparisonMedian(result);

                if (ReferenceEquals(result, baseline))
                    continue;

                // The paired per-replicate ratio when the run had replicates to pair, because each of
                // those ratios was formed inside one worker and so has that worker's own CPU draw and
                // memory layout divided out. Dividing the two aggregated medians instead leaves every
                // worker-to-worker difference in the numerator and denominator independently, which is
                // how a gate comes to fail on the machine rather than on the code.
                var estimate = Stats.LogRatio.Estimate(result, baseline);
                var ratio = estimate?.Value ?? candidateMedian / baselineMedian;

                if (ratio <= threshold)
                    continue;

                regressedCandidates.Add(new RegressionCandidate(
                    result.Name,
                    candidateMedian,
                    baselineMedian,
                    ratio,
                    candidateMedian - baselineMedian,
                    estimate));

                regressedNames.Add(result.Name);
            }
        }

        if (regressedCandidates.Count == 0)
            return RegressionVerdict.None;

        regressedNames.Sort(StringComparer.Ordinal);

        return new RegressionVerdict(
            true,
            baseline.Name,
            regressedCandidates,
            regressedNames);
    }
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
internal sealed record RegressionVerdict(
    bool HasRegression,
    string BaselineName,
    IReadOnlyList<RegressionCandidate> RegressedCandidates,
    IReadOnlyList<string> RegressedNames)
{
    internal static RegressionVerdict None { get; } = new(false, "", [], []);
}

/// <summary>
///     One regressed candidate: its median, the baseline median, the ratio the gate compared, and the
///     absolute delta in nanoseconds. Built by <see cref="ThresholdCheck.Check" />.
/// </summary>
/// <param name="Ratio">
///     The ratio the threshold was applied to: the paired per-replicate estimate when
///     <paramref name="Estimate" /> is present, otherwise <c>candidate / baseline</c> (<c>NaN</c> when
///     the baseline median is zero).
/// </param>
/// <param name="Estimate">
///     The paired ratio with its interval, when the run had at least two replicates to pair.
///     <c>null</c> for a single-launch run, where a ratio has no interval to report.
///     <para>
///         Worth reading before acting on a failure: an interval that contains 1.0 means this run
///         cannot distinguish the two benchmarks at all, so the gate tripped on a number the data does
///         not support. Raising <c>--launch-count</c> is the remedy.
///     </para>
/// </param>
internal sealed record RegressionCandidate(
    string Name,
    double CandidateMedian,
    double BaselineMedian,
    double Ratio,
    double DeltaNs,
    RatioEstimate? Estimate = null);
