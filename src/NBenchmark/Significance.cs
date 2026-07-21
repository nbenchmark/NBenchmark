namespace NBenchmark.Stats;

/// <summary>
///     Applies statistical significance testing to a set of benchmark results.
///     Public so external consumers (for example NBenchmark.Studio) can reuse the
///     same Mann-Whitney / Kruskal-Wallis pipeline that the engine uses for its
///     significance comparisons, avoiding duplication of the baseline selection
///     (via <see cref="BenchmarkResult.IsBaseline" /> or fastest-by-median fallback)
///     and the p-value verdict assignment logic.
/// </summary>
public static class Significance
{
    public static void ApplyIfEnabled(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples,
        MeasurementOptions options)
    {
        if (!options.EnableSignificance)
            return;

        if (results.Count(r => !r.Errored) < 2)
            return;

        ComputeSignificance(results, rawSamples, options.ResolveSignificanceTest(), options.SignificanceLevel, options.MinimumPracticalEffect);
    }

    /// <summary>
    ///     Computes significance verdicts using <see cref="DefaultSignificanceTest" /> and
    ///     updates <paramref name="results" /> in place. Convenience overload that selects the
    ///     engine's default strategy (Mann-Whitney U for two groups, Kruskal-Wallis for three
    ///     or more).
    /// </summary>
    public static void ComputeSignificance(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples,
        double significanceLevel = 0.05,
        double? minimumPracticalEffect = null) =>
        ComputeSignificance(results, rawSamples, DefaultSignificanceTest.Instance, significanceLevel, minimumPracticalEffect);

    /// <summary>
    ///     Runs <paramref name="test" /> over the successful results and updates
    ///     <paramref name="results" /> <b>in place</b>: pairwise verdicts replace the matching
    ///     element's <see cref="BenchmarkResult.PValue" /> and
    ///     <see cref="BenchmarkResult.SignificanceVerdict" />, and any omnibus verdict is
    ///     attached to every successful element's <see cref="BenchmarkResult.Omnibus" />.
    /// </summary>
    public static void ComputeSignificance(
        List<BenchmarkResult> results,
        Dictionary<string, double[]> rawSamples,
        ISignificanceTest test,
        double significanceLevel = 0.05,
        double? minimumPracticalEffect = null)
    {
        var successful = results.Where(r => !r.Errored).ToList();

        if (successful.Count == 0)
            return;

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                       ?? successful.MinBy(r => r.LaunchStatistics?.LaunchMedian ?? r.Median)!;

        var groups = new List<SampleGroup>();
        var baselineIndex = -1;

        foreach (var result in successful)
        {
            if (!rawSamples.TryGetValue(result.Name, out var samples))
            {
                // The result has no captured samples, so it cannot participate. Surface it
                // on the result instead of dropping it from the comparison silently.
                AppendWarning(results, result.Name,
                    $"No raw samples were captured for '{result.Name}', so it was excluded from significance testing.");

                continue;
            }

            if (result == baseline)
                baselineIndex = groups.Count;

            groups.Add(new SampleGroup(result.Name, samples, result == baseline));
        }

        if (baselineIndex < 0 || groups.Count < 2)
        {
            // A comparison was expected (two or more successful benchmarks) but could not run
            // because the baseline or too many candidates had no captured samples. Warn rather
            // than disabling significance silently. A lone successful benchmark legitimately
            // has nothing to compare against, so it needs no warning.
            if (successful.Count >= 2)
            {
                AppendWarning(results, baseline.Name,
                    $"Significance testing was skipped: fewer than two benchmarks had captured raw samples "
                    + $"(baseline '{baseline.Name}').");
            }

            return;
        }

        var context = new SignificanceContext
        {
            Groups = groups,
            BaselineIndex = baselineIndex,
            SignificanceLevel = significanceLevel,
        };

        var report = test.Analyze(context);

        ApplyReport(results, report, minimumPracticalEffect);
    }

    private static void ApplyReport(
        List<BenchmarkResult> results,
        SignificanceReport report,
        double? minimumPracticalEffect)
    {
        if (report.Pairwise.Count > 0)
        {
            var byName = report.Pairwise.ToDictionary(p => p.Name);

            for (var i = 0; i < results.Count; i++)
            {
                if (byName.TryGetValue(results[i].Name, out var comparison))
                {
                    // Apply MinimumPracticalEffect uniformly across all ISignificanceTest
                    // implementations: when the reported practical effect is below the
                    // configured threshold, the Sig verdict is downgraded to
                    // NotSignificant and the effect magnitude is forced to "neg" so a
                    // sub-threshold result is not reported as practically large.
                    var verdict = comparison.Verdict;
                    var effect = comparison.Effect;
                    var downgraded = false;

                    if (minimumPracticalEffect.HasValue
                        && comparison.Effect is { PracticalValue: { } practicalValue }
                        && !double.IsNaN(practicalValue)
                        && practicalValue < minimumPracticalEffect.Value)
                    {
                        if (verdict == SignificanceVerdict.Significant)
                        {
                            verdict = SignificanceVerdict.NotSignificant;
                            downgraded = true;
                        }

                        effect = comparison.Effect.Value with { Magnitude = "neg" };
                    }

                    // When the practical-effect gate flips a ✓ to ✗, record it so the change is
                    // discoverable rather than silently swallowing a statistically significant
                    // result. The warning surfaces in every reporter's warnings footer.
                    var warnings = results[i].Warnings;

                    if (downgraded)
                    {
                        var metric = comparison.Effect!.Value.Metric;
                        var practical = comparison.Effect.Value.PracticalValue!.Value;
                        warnings =
                        [
                            .. warnings,
                            $"statistically significant but practically negligible: {metric} practical "
                            + $"magnitude {practical:0.###} is below the minimum practical effect "
                            + $"{minimumPracticalEffect!.Value:0.###}, so the significance verdict was "
                            + "downgraded to not-significant. Set MinimumPracticalEffect = 0 "
                            + "(CLI: --min-practical-effect 0) to restore p-value-only verdicts.",
                        ];
                    }

                    results[i] = results[i] with
                    {
                        PValue = comparison.PValue,
                        SignificanceVerdict = verdict,
                        Effect = effect,
                        MedianShift = comparison.Shift,
                        Warnings = warnings,
                    };
                }
            }
        }

        if (report.Omnibus is { } omnibus)
        {
            for (var i = 0; i < results.Count; i++)
            {
                if (!results[i].Errored)
                    results[i] = results[i] with { Omnibus = omnibus };
            }
        }
    }

    private static void AppendWarning(List<BenchmarkResult> results, string name, string warning)
    {
        for (var i = 0; i < results.Count; i++)
        {
            if (results[i].Name == name)
            {
                results[i] = results[i] with { Warnings = [.. results[i].Warnings, warning] };
                return;
            }
        }
    }
}
