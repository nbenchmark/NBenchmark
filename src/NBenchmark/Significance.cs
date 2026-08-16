using NBenchmark.Engine;

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

        ComputeSignificance(results, rawSamples, options.ResolveSignificanceTest(), options.SignificanceLevel, options.MinimumPracticalEffect, options.MinimumRelativeShift, options.DriftCanary.MinimumReportableDrift);
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
        double? minimumPracticalEffect = null,
        double? minimumRelativeShift = null,
        double? minimumReportableDrift = null) =>
        ComputeSignificance(results, rawSamples, DefaultSignificanceTest.Instance, significanceLevel, minimumPracticalEffect, minimumRelativeShift, minimumReportableDrift);

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
        double? minimumPracticalEffect = null,
        double? minimumRelativeShift = null,
        double? minimumReportableDrift = null)
    {
        var successful = results.Where(r => !r.Errored).ToList();

        if (successful.Count == 0)
            return;

        // The sample lookup, group construction and write-back below all key on Name. Two
        // successful results sharing a Name are not two benchmarks to compare: they are one
        // benchmark counted twice, and feeding them through collapses both onto a single
        // sample set (the first rawSamples entry wins) and then stamps the same verdict onto
        // both rows. That is silent corruption, not a comparison. Refuse it here with a message
        // that names the duplicate and points at the remedy, rather than letting the write-back
        // dictionary throw its generic "An item with the same key has already been added" much
        // later, or worse, corrupting the results when the write-back happens to have one entry.
        var seen = new HashSet<string>(successful.Count);
        foreach (var result in successful)
        {
            if (!seen.Add(result.Name))
            {
                throw new ArgumentException(
                    $"Significance testing requires unique benchmark names, but '{result.Name}' appears "
                    + "more than once among the successful results. Two results sharing a name collapse "
                    + "onto one sample set and one verdict, which is silent corruption rather than a "
                    + "comparison. Disambiguate the benchmarks (for example by qualifying the class name "
                    + "with its full namespace) so every result has a unique Name.",
                    nameof(results));
            }
        }

        var baseline = successful.FirstOrDefault(r => r.IsBaseline)
                       ?? successful.MinBy(ComparisonGroup.ComparisonMedian)!;

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

        ApplyReport(results, report, baseline, minimumPracticalEffect, minimumRelativeShift, significanceLevel, minimumReportableDrift);
    }

    private static void ApplyReport(
        List<BenchmarkResult> results,
        SignificanceReport report,
        BenchmarkResult baseline,
        double? minimumPracticalEffect,
        double? minimumRelativeShift,
        double significanceLevel,
        double? minimumReportableDrift)
    {
        var baselineMedian = ComparisonGroup.ComparisonMedian(baseline);
        if (report.Pairwise.Count > 0)
        {
            var byName = report.Pairwise.ToDictionary(p => p.Name);

            for (var i = 0; i < results.Count; i++)
            {
                if (byName.TryGetValue(results[i].Name, out var comparison))
                {
                    // Two gates, applied in addition to the test's own verdict:
                    //
                    //   1. MinimumPracticalEffect - the consistency gate. |Cliff's delta| below the
                    //      threshold means the candidate only barely differs from the baseline, so a
                    //      Significant verdict is downgraded to NotSignificant and the magnitude label
                    //      is forced to "neg" so a sub-threshold result is not reported as large.
                    //
                    //   2. MinimumRelativeShift - the magnitude gate. |median shift| / baseline median
                    //      below the threshold means the change, however consistent, is too small to
                    //      act on (the sub-percent noise measured with near-zero spread that the U test
                    //      rejects and Cliff's delta scores as large). The verdict is downgraded but the
                    //      magnitude label is left alone, because a small shift and a large consistency
                    //      are independent facts.
                    //
                    // A ✓ therefore means "real, at least a small effect, and at least a
                    // MinimumRelativeShift relative shift". Either gate alone can flip it to ✗.
                    var verdict = comparison.Verdict;
                    var effect = comparison.Effect;
                    var downgradedPractical = false;
                    var downgradedShift = false;

                    // The Hodges-Lehmann shift (candidate - baseline, in time units), when the
                    // strategy produced one. Hoisted out of the gate so the warning below can name
                    // the value that tripped it.
                    double? shiftValue = comparison.Shift is { Value: { } sv } && !double.IsNaN(sv) ? sv : null;

                    if (minimumPracticalEffect.HasValue
                        && comparison.Effect is { PracticalValue: { } practicalValue }
                        && !double.IsNaN(practicalValue)
                        && practicalValue < minimumPracticalEffect.Value)
                    {
                        if (verdict == SignificanceVerdict.Significant)
                        {
                            verdict = SignificanceVerdict.NotSignificant;
                            downgradedPractical = true;
                        }

                        effect = comparison.Effect.Value with { Magnitude = "neg" };
                    }

                    if (minimumRelativeShift.HasValue
                        && shiftValue is { } shift
                        && baselineMedian > 0
                        && Math.Abs(shift) / baselineMedian < minimumRelativeShift.Value)
                    {
                        if (verdict == SignificanceVerdict.Significant)
                        {
                            verdict = SignificanceVerdict.NotSignificant;
                            downgradedShift = true;
                        }
                    }

                    // The launch-blocked verdict: a paired one-sample Student-t on the per-launch
                    // log-ratios over the k launches (reusing LogRatio.Estimate), at the run's
                    // significance level. The pooled verdict above runs on every raw sample
                    // concatenated across launches, so it inherits the power of the pooled count
                    // and flags a between-launch location offset at full n regardless of whether
                    // the code differs. The launch-blocked test answers the question the reader is
                    // actually asking: does the difference reproduce across launches, or is it one
                    // process draw read as a code change? NotTested when fewer than two launches
                    // can be paired (single-launch runs), because one pair is a ratio, not an
                    // estimate of one.
                    var launchBlocked = LaunchBlockedVerdict(results[i], baseline, significanceLevel);

                    // When a gate flips a ✓ to ✗, record it so the change is discoverable rather
                    // than silently swallowing a statistically significant result. The warning
                    // surfaces in every reporter's warnings footer.
                    var warnings = results[i].Warnings;

                    if (downgradedPractical)
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

                    if (downgradedShift)
                    {
                        var relative = Math.Abs(shiftValue!.Value) / baselineMedian;
                        warnings =
                        [
                            .. warnings,
                            $"statistically significant but the shift is too small to act on: the "
                            + $"relative median shift {relative:0.###} (|{shiftValue.Value:0.###}| / "
                            + $"baseline median {baselineMedian:0.###}) is below the minimum relative "
                            + $"shift {minimumRelativeShift!.Value:0.###}, so the significance verdict "
                            + "was downgraded to not-significant. Set MinimumRelativeShift = 0 "
                            + "(CLI: --min-relative-shift 0) to disable the relative-shift gate.",
                        ];
                    }

                    // The pooled verdict survived the gates but the launches do not separate the
                    // two: the difference is reproducibility-only, not a code change. Name it as
                    // such rather than letting the ✓ be read as a real effect. This is the
                    // consequence W-41 makes legible below the ProcessVarianceRatio > 4 threshold
                    // that DescribeReproducibility alone would miss - it fires whenever the paired
                    // per-launch test cannot rule out a ratio of 1.0, however quiet the run.
                    if (verdict == SignificanceVerdict.Significant
                        && launchBlocked == SignificanceVerdict.NotSignificant)
                    {
                        warnings =
                        [
                            .. warnings,
                            "statistically significant on the pooled samples, but the per-launch "
                            + "paired test does not reproduce the difference across launches (the "
                            + "paired ratio interval spans 1.00x), so it is a reproducibility-only "
                            + "difference rather than a code change. Compare the per-launch medians "
                            + "and raise --launch-count to tighten the reproducibility estimate.",
                        ];
                    }

                    // The host drift canary's one output. Unlike the two gates above this never
                    // touches the verdict - the canary measures the machine, not the comparison,
                    // and a downgrade would be acting on indirect evidence. It fires on the
                    // reported median difference rather than the Hodges-Lehmann shift because
                    // that is the number the ratio column shows, and because it is present even
                    // when the strategy produced no shift estimate.
                    if (baselineMedian > 0)
                    {
                        var reportedShift =
                            Math.Abs(ComparisonGroup.ComparisonMedian(results[i]) - baselineMedian) / baselineMedian;

                        var driftWarning = HostDrift.Describe(
                            results[i],
                            baseline,
                            reportedShift,
                            minimumReportableDrift ?? DriftCanaryOptions.DefaultMinimumReportableDrift);

                        if (driftWarning is not null)
                            warnings = [.. warnings, driftWarning];
                    }

                    results[i] = results[i] with
                    {
                        PValue = comparison.PValue,
                        SignificanceVerdict = verdict,
                        Effect = effect,
                        MedianShift = comparison.Shift,
                        LaunchBlockedVerdict = launchBlocked,
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

    /// <summary>
    ///     The launch-blocked verdict for one candidate versus the baseline: a paired one-sample
    ///     Student-t on the per-launch log-ratios (reusing
    ///     <see cref="LogRatio.Estimate(NBenchmark.BenchmarkResult,NBenchmark.BenchmarkResult,System.Double)" />)
    ///     at <c>1 - significanceLevel</c>. An interval spanning 1.0 means the launches do not
    ///     separate the two, so a pooled Significant verdict is reproducibility-only rather than a
    ///     code change. <see cref="SignificanceVerdict.NotTested" /> when fewer than two launches
    ///     can be paired.
    /// </summary>
    private static SignificanceVerdict LaunchBlockedVerdict(
        BenchmarkResult candidate,
        BenchmarkResult baseline,
        double significanceLevel)
    {
        var ratio = LogRatio.Estimate(candidate, baseline, 1.0 - significanceLevel);

        return ratio is null
            ? SignificanceVerdict.NotTested
            : ratio.IncludesUnity
                ? SignificanceVerdict.NotSignificant
                : SignificanceVerdict.Significant;
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
