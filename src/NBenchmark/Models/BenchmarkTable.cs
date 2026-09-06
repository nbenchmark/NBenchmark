using System.Globalization;
using NBenchmark.Engine;
using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

public sealed record BenchmarkTable
{
    /// <summary>
    ///     When set by the harness, <see cref="BuildPerClass" /> returns a single combined table
    ///     with a <see cref="BenchmarkResult.ClassName" /> column instead of one table per class.
    ///     The harness sets this before calling reporters and clears it after.
    /// </summary>
    /// <remarks>
    ///     Internal, and read only while a table is being built: every table stamps the mode it was
    ///     built under onto <see cref="CrossClass" />, so a reporter asks the table in front of it
    ///     rather than a process-wide flag that may since have been cleared.
    /// </remarks>
    internal static bool CrossClassMode { get; set; }

    /// <summary>
    ///     <c>true</c> when this table combines benchmarks from more than one class, so
    ///     <see cref="BenchmarkResult.ClassName" /> distinguishes the rows and reporters should render
    ///     it as a column.
    /// </summary>
    public bool CrossClass { get; init; }

    public required IReadOnlyList<BenchmarkRow> Rows { get; init; }
    /// <summary>
    ///     When the run this table summarises started. <c>null</c> for an empty table, which has no
    ///     run behind it to date.
    /// </summary>
    /// <remarks>
    ///     Was a preformatted <c>string</c>, which meant the same name carried a different type on
    ///     sibling records - <see cref="BenchmarkResult.RunAtUtc" /> is the instant - and every
    ///     consumer that wanted to sort, compare or reformat had to parse the table's own rendering
    ///     back out. Reporters format it; the model carries the value.
    /// </remarks>
    public required DateTimeOffset? RunAtUtc { get; init; }
    public required int WarmupSamples { get; init; }
    public required int SampleCount { get; init; }
    public required double ConfidenceLevel { get; init; }
    public required string OutlierDetectorName { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public double SignificanceLevel { get; init; } = 0.05;

    /// <summary>The display name of the pairwise significance strategy used (e.g. Mann-Whitney U).</summary>
    public string SignificanceTestName { get; init; } = DefaultSignificanceTest.Instance.Name;

    /// <summary>The measurement profile under which the run was produced.</summary>
    public GcBehavior GcBehavior { get; init; } = GcBehavior.Natural;

    /// <summary>
    ///     The runtime-startup configuration these results were actually measured under, by name.
    ///     <c>"host"</c> means the measuring process inherited whatever configuration it was
    ///     started with, which is always the case for in-process benchmarks.
    /// </summary>
    public string RuntimeProfileName { get; init; } = RuntimeProfile.Host.Name;

    /// <summary>
    ///     The startup knobs in effect, e.g. <c>"tiered=off pgo=off r2r=off"</c>. Empty when none
    ///     are set.
    /// </summary>
    public string RuntimeKnobs { get; init; } = "";

    /// <summary>
    ///     <c>true</c> when the rows in this table were not all measured under the same runtime
    ///     profile - for example a class combining <c>[Isolation(Isolation.Off)]</c> benchmarks with isolated
    ///     ones. Their numbers are not comparable with each other, and reporters must say so:
    ///     the profile difference alone was measured to move a value by roughly 3.3x, which is far
    ///     larger than most effects anyone is looking for.
    /// </summary>
    public bool MixedRuntimeProfiles { get; init; }

    /// <summary>
    ///     The distinct reasons any row in this table was measured in the host process rather than a
    ///     worker. Empty when every row was isolated.
    ///     <para>
    ///         Reported because <see cref="MixedRuntimeProfiles" /> only says the rows are not
    ///         comparable; this says <i>why</i>, and each reason has a different remedy. A user who
    ///         cannot see the difference between "you asked for in-process" and "the worker is not
    ///         installed" has no way to act on either.
    ///     </para>
    /// </summary>
    public IReadOnlyList<IsolationStatus> InProcessReasons { get; init; } = [];

    /// <summary>
    ///     <c>true</c> when reporters should add a per-row isolation column: either the rows disagree
    ///     about where they ran, or isolation was <b>refused</b> for at least one of them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Mixed statuses alone is not the right rule, and it failed in the direction that matters
    ///         most: a table where <i>every</i> row fell back has one distinct status, so the column
    ///         was suppressed for exactly the run a reader is most likely to misread as isolated.
    ///     </para>
    ///     <para>
    ///         Refusal rather than "not isolated", because a run under <c>--in-process</c> or
    ///         <c>--dry-run</c> is uniformly host-measured on purpose. Adding a column that reads "no"
    ///         on every row there says nothing the footer does not, and it costs the bar column, which
    ///         reporters trade against it.
    ///     </para>
    /// </remarks>
    public bool MixedIsolationStatuses { get; init; }

    /// <summary>
    ///     The omnibus significance verdict (e.g. Kruskal-Wallis) across all benchmarks, when
    ///     an omnibus test was run (three or more groups); otherwise <c>null</c>.
    /// </summary>
    public OmnibusComparison? Omnibus { get; init; }

    /// <summary>
    ///     The ordered set of parameter names present across the rows of this table, when the
    ///     benchmarks are parameterised; empty otherwise. Reporters render one column per name so a
    ///     parameter sweep can be read and compared within a single table.
    /// </summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    public static BenchmarkTable Build(IReadOnlyList<BenchmarkResult> results)
    {
        var successful = results.Where(r => !r.Errored).ToList();
        var multiBenchmark = results.Count > 1;

        BenchmarkResult? baseline = null;

        if (successful.Count > 0)
            baseline = ComparisonGroup.PickBaseline(successful);

        return BuildInternal(results, baseline, multiBenchmark);
    }

    public static IReadOnlyList<BenchmarkTable> BuildPerClass(IReadOnlyList<BenchmarkResult> results)
    {
        if (results.Count == 0)
            return [BuildInternal(results, null, false)];

        // Cross-class mode: return a single combined table with a ClassName column.
        if (CrossClassMode)
        {
            var anyParam = results.Any(r => r.ParameterSet.Count > 0);

            if (anyParam)
                return [BuildParameterised(results)];

            var successful = results.Where(r => !r.Errored).ToList();
            var baseline = ComparisonGroup.PickBaseline(successful);
            return [BuildInternal(results, baseline, results.Count > 1)];
        }

        var anyParameterised = results.Any(r => r.ParameterSet.Count > 0);

        var byClass = results
            .GroupBy(r => r.ClassName)
            .ToList();

        // Fast path: a single non-parameterised class renders as one flat comparison table.
        if (!anyParameterised && byClass.Count <= 1)
            return [Build(results)];

        var tables = new List<BenchmarkTable>(byClass.Count);

        foreach (var group in byClass)
        {
            var groupResults = group.ToList();

            if (groupResults.Any(r => r.ParameterSet.Count > 0))
            {
                tables.Add(BuildParameterised(groupResults));
                continue;
            }

            var successful = groupResults.Where(r => !r.Errored).ToList();
            var baseline = ComparisonGroup.PickBaseline(successful);
            tables.Add(BuildInternal(groupResults, baseline, groupResults.Count > 1));
        }

        return tables;
    }

    /// <summary>
    ///     Builds a single comparison table for a parameterised benchmark group (one class in Harness
    ///     mode, or the whole suite in suite mode). Parameter values become columns; rows are grouped
    ///     by parameter set in first-appearance order and ordered by median within each group, with
    ///     the baseline, ratio and significance computed independently per parameter group.
    /// </summary>
    private static BenchmarkTable BuildParameterised(IReadOnlyList<BenchmarkResult> results)
    {
        var parameterNames = CollectParameterNames(results);

        // GroupBy preserves first-appearance order of keys, which mirrors expansion order.
        var groups = results
            .GroupBy(r => BenchmarkParameter.GetKey(r.ParameterSet))
            .ToList();

        var rows = new List<BenchmarkRow>(results.Count);

        // A within-group comparison exists only when some parameter group holds two or more
        // benchmarks (e.g. competing methods measured at the same parameters). When no group
        // does - a single method swept across parameter values - rank the whole table against
        // its reference point so the Ratio column conveys the scaling factor across the sweep.
        var anyGroupComparison = groups.Any(g =>
            g.GroupBy(r => r.TargetFramework, StringComparer.Ordinal)
                .Any(runtimeGroup => runtimeGroup.Count(r => !r.Errored) > 1));

        if (!anyGroupComparison)
        {
            foreach (var runtimeGroup in results.GroupBy(r => r.TargetFramework, StringComparer.Ordinal))
            {
                var runtimeResults = runtimeGroup.ToList();
                var successful = runtimeResults.Where(r => !r.Errored).ToList();
                BenchmarkResult? reference = null;

                if (successful.Count > 0)
                {
                    var explicitBaselines = successful.Where(r => r.IsBaseline).ToList();

                    // Honour a single explicit baseline; otherwise scale against the fastest point so
                    // ratios read naturally as 1.00x (fastest) up to the slowest parameter value.
                    reference = explicitBaselines.Count == 1
                        ? explicitBaselines[0]
                        : ComparisonGroup.PickBaseline(successful);
                }

                foreach (var result in runtimeResults.OrderBy(r => r.MedianNs))
                {
                    rows.Add(BuildRow(
                        result,
                        reference,
                        false,
                        true,
                        reference is not null && ReferenceEquals(result, reference)));
                }
            }

            return AssembleTable(results, rows, parameterNames);
        }

        foreach (var group in groups)
        {
            foreach (var runtimeGroup in group.GroupBy(r => r.TargetFramework, StringComparer.Ordinal))
            {
                var runtimeResults = runtimeGroup.ToList();
                var successful = runtimeResults.Where(r => !r.Errored).ToList();
                var baseline = ComparisonGroup.PickBaseline(successful);
                var multiBenchmark = successful.Count > 1;

                foreach (var result in runtimeResults.OrderBy(r => r.MedianNs))
                {
                    rows.Add(BuildRow(result, baseline, multiBenchmark, multiBenchmark));
                }
            }
        }

        return AssembleTable(results, rows, parameterNames);
    }

    private static IReadOnlyList<string> CollectParameterNames(IReadOnlyList<BenchmarkResult> results)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var result in results)
        foreach (var parameter in result.ParameterSet)
        {
            if (seen.Add(parameter.Name))
                names.Add(parameter.Name);
        }

        return names;
    }

    private static bool HasMultipleRuntimes(IReadOnlyList<BenchmarkResult> results)
        => results
            .Select(r => r.TargetFramework)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .Count() > 1;

    private static BenchmarkTable BuildInternal(
        IReadOnlyList<BenchmarkResult> results,
        BenchmarkResult? baseline,
        bool multiBenchmark)
    {
        if (HasMultipleRuntimes(results))
        {
            var rowsByRuntime = new List<BenchmarkRow>(results.Count);

            foreach (var runtimeGroup in results.GroupBy(r => r.TargetFramework, StringComparer.Ordinal))
            {
                var runtimeResults = runtimeGroup.ToList();
                var successful = runtimeResults.Where(r => !r.Errored).ToList();
                var runtimeBaseline = ComparisonGroup.PickBaseline(successful);
                var runtimeMultiBenchmark = runtimeResults.Count > 1;

                foreach (var result in runtimeResults.OrderBy(r => r.MedianNs))
                {
                    rowsByRuntime.Add(BuildRow(result, runtimeBaseline, runtimeMultiBenchmark));
                }
            }

            return AssembleTable(results, rowsByRuntime, []);
        }

        var rows = results
            .OrderBy(r => r.MedianNs)
            .Select(r => BuildRow(r, baseline, multiBenchmark))
            .ToList();

        return AssembleTable(results, rows, []);
    }

    private static BenchmarkTable AssembleTable(
        IReadOnlyList<BenchmarkResult> results,
        IReadOnlyList<BenchmarkRow> rows,
        IReadOnlyList<string> parameterNames)
    {
        var headerSource = results.FirstOrDefault(r => !r.Errored) ?? results.FirstOrDefault();

        return new BenchmarkTable
        {
            Rows = rows,
            CrossClass = CrossClassMode,
            ParameterNames = parameterNames,
            RunAtUtc = headerSource?.RunAtUtc,
            WarmupSamples = headerSource?.WarmupSamples ?? 0,
            SampleCount = headerSource?.SampleCount ?? 0,
            ConfidenceLevel = headerSource?.ConfidenceLevel ?? 0.95,
            OutlierDetectorName = results.FirstOrDefault()?.OutlierDetectorName ?? OutlierDetectors.IqrFence.Name,
            TotalDuration = results.Aggregate(TimeSpan.Zero, (a, r) => a + r.TotalDuration),
            SignificanceLevel = headerSource?.SignificanceLevel ?? 0.05,
            SignificanceTestName = headerSource?.SignificanceTestName ?? DefaultSignificanceTest.Instance.Name,
            GcBehavior = results.FirstOrDefault()?.GcBehavior ?? GcBehavior.Natural,
            RuntimeProfileName = results.FirstOrDefault()?.RuntimeProfileName ?? RuntimeProfile.Host.Name,
            RuntimeKnobs = results.FirstOrDefault()?.RuntimeKnobs ?? "",
            MixedRuntimeProfiles = results
                .Select(r => r.RuntimeProfileName)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1,
            // Errored rows are excluded from both: a benchmark that threw was not measured anywhere,
            // so it has no provenance to report. Including them meant a single errored row in an
            // otherwise fully-isolated run flipped MixedIsolationStatuses - adding a spurious column
            // and, because reporters trade the two, removing the bar column.
            InProcessReasons = results
                .Where(r => !r.Errored && !r.IsolationStatus.IsIsolated())
                .Select(r => r.IsolationStatus)
                .Distinct()
                .OrderBy(s => s)
                .ToList(),
            MixedIsolationStatuses =
                results.Any(r => !r.Errored && r.IsolationStatus.IsRefusal())
                || results
                    .Where(r => !r.Errored)
                    .Select(r => r.IsolationStatus)
                    .Distinct()
                    .Count() > 1,
            Omnibus = results.FirstOrDefault(r => r.Omnibus is not null)?.Omnibus,
        };
    }

    private static BenchmarkRow BuildRow(
        BenchmarkResult result,
        BenchmarkResult? baseline,
        bool multiBenchmark,
        bool comparable = true,
        bool? isBaselineOverride = null)
    {
        // A ratio between two different comparison groups is not a measurement of the two bodies.
        // The dominant term across a process boundary is the runtime configuration, worth ~3.3x on
        // bodies of provably identical cost, so dividing an in-process median by an isolated one
        // reports that configuration difference under the name of a speedup. Significance testing
        // already partitions on this key; the ratio column did not, and printed a number.
        var mixedGroup = baseline is not null
                         && !ReferenceEquals(result, baseline)
                         && !ComparisonGroup.SameGroup(result, baseline);

        var ratio = comparable && !mixedGroup ? RatioFor(result, baseline) : null;

        return new BenchmarkRow
        {
            Result = result,
            BaseName = ComputeBaseName(result),
            Ratio = ratio?.Value ?? (comparable && !mixedGroup ? ComputeRatio(result, baseline) : double.NaN),
            RatioEstimate = ratio,
            RatioSuppressed = mixedGroup,
            SignificanceLabel = ComputeSignificanceLabel(result, multiBenchmark),
            IsBaseline = comparable && (isBaselineOverride ?? result.IsBaseline),
        };
    }

    private static string ComputeBaseName(BenchmarkResult result)
    {
        if (result.ParameterSet.Count == 0)
            return result.Name;

        var suffix = $"({BenchmarkParameter.FormatLabel(result.ParameterSet)})";

        return result.Name.EndsWith(suffix, StringComparison.Ordinal)
            ? result.Name[..^suffix.Length]
            : result.Name;
    }

    /// <summary>
    ///     The paired per-replicate ratio against the baseline, or <c>null</c> when the run had too few
    ///     replicates to form one and the plain ratio of medians is all there is.
    /// </summary>
    /// <remarks>
    ///     Preferred over the ratio of medians whenever it exists, because a comparison group is
    ///     measured co-resident in one worker per replicate - so pairing replicate <i>i</i> against
    ///     replicate <i>i</i> divides that worker's own CPU draw and memory layout out of the ratio.
    ///     That co-residency is the statistical reason the group shares a worker at all, and dividing
    ///     the two aggregated medians discarded the benefit at the last step.
    /// </remarks>
    private static RatioEstimate? RatioFor(BenchmarkResult result, BenchmarkResult? baseline)
    {
        if (result.Errored || baseline is null || baseline.Errored || ReferenceEquals(result, baseline))
            return null;

        return LogRatio.Estimate(result, baseline);
    }

    private static double ComputeRatio(BenchmarkResult result, BenchmarkResult? baseline)
    {
        if (result.Errored || baseline is null || baseline.MedianNs == 0)
            return double.NaN;

        return result.MedianNs / baseline.MedianNs;
    }

    private static string ComputeSignificanceLabel(BenchmarkResult result, bool multiBenchmark)
    {
        if (result.Errored || !multiBenchmark || result.IsBaseline || result.SignificanceVerdict == SignificanceVerdict.NotTested)
            return "";

        return result.SignificanceVerdict == SignificanceVerdict.Significant ? "✓" : "✗";
    }

    public static string RenderStatsBlock(BenchmarkRow row, ReportDetail detail)
    {
        if (detail != ReportDetail.Advanced)
            return "";

        var lines = new List<string>();

        lines.Add($"Samples: {row.Result.SampleCount} measured (warmup: {row.Result.WarmupSamples}, pre-trim: {row.Result.SampleCount + row.Result.OutliersRemoved})");

        if (row.Result.OutliersRemoved > 0)
        {
            var label = row.Result.OutliersRemoved == 1 ? "outlier" : "outliers";
            lines.Add($"Outliers: {row.Result.OutliersRemoved} {label} removed");
        }

        lines.Add($"Range: {BenchmarkFormatter.FormatNs(row.Result.RangeNs)} ({BenchmarkFormatter.FormatNs(row.Result.MinNs)} -> {BenchmarkFormatter.FormatNs(row.Result.MaxNs)})");

        lines.Add(
            $"Quartiles: Q1 = {BenchmarkFormatter.FormatNs(row.Result.Q1Ns)}, Q3 = {BenchmarkFormatter.FormatNs(row.Result.Q3Ns)}, IQR = {BenchmarkFormatter.FormatNs(row.Result.InterquartileRangeNs)}");

        if (row.Result.LowerFenceNs is not null && row.Result.UpperFenceNs is not null)
            lines.Add($"Fences: [{BenchmarkFormatter.FormatNs(row.Result.LowerFenceNs.Value)}; {BenchmarkFormatter.FormatNs(row.Result.UpperFenceNs.Value)}]");

        lines.Add(
            $"CI: [{BenchmarkFormatter.FormatNs(row.Result.ConfidenceIntervalLowerNs)}; {BenchmarkFormatter.FormatNs(row.Result.ConfidenceIntervalUpperNs)}] (CI {row.Result.ConfidenceLevel * 100:F1}%)");

        if (row.Result.MedianConfidenceIntervalLowerNs is { } medianLower && row.Result.MedianConfidenceIntervalUpperNs is { } medianUpper)
        {
            // With more than one launch this interval is the between-launch reproducibility
            // interval (the Student-t band over the k launch medians); with one launch it is the
            // within-launch distribution-free interval the single process measured. The label
            // says which, so the number is not read as one kind of interval when it is the other.
            var betweenLaunch = row.Result.LaunchStatistics is { LaunchCount: > 1 };
            var source = betweenLaunch
                ? $"between-launch over {row.Result.LaunchStatistics!.LaunchCount} launches"
                : "distribution-free";

            lines.Add(
                $"Median CI: [{BenchmarkFormatter.FormatNs(medianLower)}; {BenchmarkFormatter.FormatNs(medianUpper)}] "
                + $"({source}, CI {row.Result.ConfidenceLevel * 100:F1}%)");
        }

        lines.Add($"Margin: ±{BenchmarkFormatter.FormatNs(row.Result.MarginOfErrorNs)} ({row.Result.MarginOfErrorPercent:F2}% of mean)");

        if (row.RatioEstimate is { } ratio)
        {
            // Worth its own line rather than a suffix in the table cell, because it answers a
            // different question from the ratio. The ratio says how much slower; this says whether the
            // run can tell at all. An interval spanning 1.00 means it cannot, however far the point
            // estimate sits from it.
            var verdict = ratio.IncludesUnity
                ? " - spans 1.00x, so this run cannot distinguish the two"
                : "";

            lines.Add(
                $"Ratio: {ratio.Value:0.00}x [{ratio.FormatInterval()}] "
                + $"(paired across {ratio.Replicates} launches, CI {ratio.ConfidenceLevel * 100:F1}%){verdict}");
        }

        if (row.Result.LaunchBlockedVerdict != SignificanceVerdict.NotTested)
        {
            // The formal significance companion to the ratio line above: the paired per-launch
            // Student-t at the run's significance level, reported beside the pooled verdict so a
            // reproducibility-only difference is named as such rather than read as a code change.
            // A ✓ here means the launches reproduce the difference; a ✗ means they do not, however
            // significant the pooled test called it.
            var label = row.Result.LaunchBlockedVerdict == SignificanceVerdict.Significant ? "✓" : "✗";
            lines.Add($"Launch verdict: {label} (paired per-launch Student-t)");
        }

        lines.Add($"CV: {row.Result.CoefficientOfVariation:F4} ({row.Result.CoefficientOfVariationPercent:F2}%)");

        var skewSuffix = row.Result.SampleCount < 3 ? " (n too small)" : "";
        lines.Add($"Skewness: {row.Result.Skewness:F4}{skewSuffix}");

        var kurtSuffix = row.Result.SampleCount < 4 ? " (n too small)" : "";
        lines.Add($"Kurtosis: {row.Result.Kurtosis:F4}{kurtSuffix}");

        lines.Add($"MAD: {BenchmarkFormatter.FormatNs(row.Result.MedianAbsoluteDeviationNs)}");

        if (row.Result.Percentiles.Count > 0)
        {
            var parts = row.Result.Percentiles.Select(e =>
            {
                var label = e.Percentile >= 1.0 ? "Max" : $"P{FormatPercentileKey(e.Percentile)}";
                return $"{label} = {BenchmarkFormatter.FormatNs(e.Value)}";
            }).ToList();

            lines.Add($"Percentiles:  {string.Join("  ", parts.Take(3))}");

            for (var i = 3; i < parts.Count; i += 3)
            {
                var chunk = parts.Skip(i).Take(3);
                lines.Add($"              {string.Join("  ", chunk)}");
            }
        }

        if (row.Result.Effect is { } effect && string.Equals(effect.Metric, EffectMetrics.CliffsDelta, StringComparison.Ordinal))
        {
            var magnitudeText = effect.Magnitude?.ToShortString() ?? "?";
            var deltaText = effect.Value.HasValue ? effect.Value.Value.ToString("F4") : "?";

            var directionText = effect.Direction switch
            {
                EffectDirection.CandidateHigher => "slower than baseline",
                EffectDirection.CandidateLower => "faster than baseline",
                _ => "similar to baseline",
            };

            lines.Add($"Cliff's \u03b4: {deltaText} ({magnitudeText}) \u2014 candidate tends to be {directionText}");
        }
        else if (row.Result.Effect is { } genericEffect)
        {
            var valueText = genericEffect.Value.HasValue ? genericEffect.Value.Value.ToString("F4") : "?";
            var magnitudeText = genericEffect.Magnitude?.ToShortString() ?? "?";
            lines.Add($"Effect ({genericEffect.Metric}): {valueText} ({magnitudeText})");
        }

        if (row.Result.MedianShift is { } shift)
            lines.Add($"Median shift (Hodges-Lehmann): {FormatShift(shift)}");

        if (row.Result.AllocatedBytesMedian is not null)
        {
            lines.Add("");
            lines.Add("Allocations:");
            lines.Add($"  Mean: {BenchmarkFormatter.FormatAlloc(row.Result.AllocatedBytesMean ?? 0)}");
            lines.Add($"  P50:  {BenchmarkFormatter.FormatAlloc(row.Result.AllocatedBytesMedian.Value)}");
            lines.Add($"  P95:  {BenchmarkFormatter.FormatAlloc(row.Result.AllocatedBytesP95 ?? 0)}");
            lines.Add($"  Max:  {BenchmarkFormatter.FormatAlloc(row.Result.AllocatedBytesMax ?? 0)}");
        }

        if (row.Result.Diagnostics is { } diag)
        {
            lines.Add("");
            lines.Add("Diagnostics:");

            if (diag.Gen0Collections.HasValue)
                lines.Add($"  Gen0: {diag.Gen0Collections.Value}   Gen1: {diag.Gen1Collections ?? 0}   Gen2: {diag.Gen2Collections ?? 0}");

            if (diag.HeapCommittedBytes.HasValue)
                lines.Add(
                    $"  Heap: {BenchmarkFormatter.FormatBytes(diag.HeapCommittedBytes.Value)} (fragmented {BenchmarkFormatter.FormatBytes(diag.HeapFragmentedBytes ?? 0)})");

            if (diag.CpuWallRatio.HasValue)
                lines.Add($"  CPU: {diag.CpuWallRatio.Value * 100:F0}% ({BenchmarkFormatter.FormatNs(diag.CpuTimeNsPerOp ?? 0)}/op)");

            if (diag.ExceptionCountPerOp.HasValue)
                lines.Add($"  Exc/op: {diag.ExceptionCountPerOp.Value:F4}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    ///     Formats the adaptive measurement diagnostic into a single human-readable summary line
    ///     (e.g. <c>auto-tuned: 240 samples × 64 ops, warmup 40, CI ±1.8%</c>). When the pre-flight
    ///     jitter probe ran, appends a jitter clause (e.g. <c>, jitter 0.04</c>); when the outlier
    ///     detector was auto-switched, appends a switch clause (e.g. <c>, detector→MAD</c>).
    ///     <para>
    ///         Also surfaces the steady-state signals when they are notable: <c>, drift 2.2%</c> once
    ///         the split-half gap is at all appreciable, <c>, restarts 1</c> when the drift gate
    ///         resampled, and <c>, warmup cut short</c> when auto-warmup never reached its time floor.
    ///         A tight CI sitting next to any of those is the visible signal that the number is precise
    ///         but not reproducible - which is exactly what a bare CI figure hides.
    ///     </para>
    /// </summary>
    public static string FormatAutoTuneSummary(AutoTuneDiagnostic diagnostic)
    {
        var summary = $"auto-tuned: {diagnostic.ResolvedSamples:N0} samples × {diagnostic.OpsPerSample:N0} ops, "
                      + $"warmup {diagnostic.ResolvedWarmup:N0}, CI ±{diagnostic.AchievedRelativeCiWidth * 100:F1}%";

        if (diagnostic.JitterMetric.HasValue)
            summary += $", jitter {diagnostic.JitterMetric.Value:F2}";

        if (diagnostic.OutlierDetectorSwitched)
            summary += ", detector→MAD";

        // Half the default drift tolerance: below that the gap is ordinary noise and reporting it would
        // be clutter on every row.
        if (diagnostic.SplitHalfDrift > 0.05)
            summary += $", drift {diagnostic.SplitHalfDrift * 100:F1}%";

        if (diagnostic.MeasurementRestarts > 0)
            summary += $", restarts {diagnostic.MeasurementRestarts}";

        if (!diagnostic.WarmupTimeFloorMet)
            summary += ", warmup cut short";

        if (diagnostic.InterferenceRejectedCount > 0)
            summary += $", {diagnostic.InterferenceRejectedCount:N0} preempted";

        return summary;
    }

    /// <summary>
    ///     Formats a Hodges-Lehmann shift and its interval as a signed, human-readable string,
    ///     e.g. <c>+12.3 ns [8.1 ns, 16.9 ns] (95%)</c>. The sign makes the direction explicit
    ///     (positive = candidate slower than baseline).
    /// </summary>
    public static string FormatShift(ShiftEstimate shift)
    {
        var sign = shift.Value >= 0 ? "+" : "-";
        var value = $"{sign}{BenchmarkFormatter.FormatNs(Math.Abs(shift.Value))}";

        return $"{value} [{BenchmarkFormatter.FormatNs(shift.Lower)}, {BenchmarkFormatter.FormatNs(shift.Upper)}] "
               + $"({shift.ConfidenceLevel * 100:0.#}%)";
    }

    /// <summary>
    ///     Formats a percentile value (0.0-1.0) into a short display key.
    ///     Examples: 0.50 -> "50", 0.95 -> "95", 0.99 -> "99", 0.999 -> "99.9", 1.0 -> "Max".
    /// </summary>
    public static string FormatPercentileKey(double p)
    {
        if (p >= 1.0)
            return "Max";

        var scaled = p * 100.0;
        var rounded = Math.Round(scaled, 1);

        return rounded == (int)rounded
            ? ((int)rounded).ToString(CultureInfo.InvariantCulture)
            : rounded.ToString("F1", CultureInfo.InvariantCulture);
    }
}

/// <summary>
///     One benchmark's place in a comparison: the measurement itself, plus everything that only
///     means something relative to the table's baseline.
/// </summary>
/// <remarks>
///     Composition rather than a parallel copy. This record once restated 58 of
///     <see cref="BenchmarkResult" />'s properties by name, which made a reporter author learn two
///     sixty-property DTOs whose relationship was written down nowhere, and made every new metric a
///     change in two places that could silently disagree. The five properties below are the ones a
///     row genuinely adds; everything else is read through <see cref="Result" />.
/// </remarks>
public record BenchmarkRow
{
    /// <summary>The measurement this row presents.</summary>
    public required BenchmarkResult Result { get; init; }

    /// <summary>
    ///     The benchmark name with any parameter suffix removed (e.g. <c>Sort</c> for
    ///     <c>Sort(size=10)</c>). Equal to <see cref="BenchmarkResult.Name" /> for non-parameterised
    ///     benchmarks. Reporters show this in the Benchmark column while parameter values appear as
    ///     columns.
    /// </summary>
    public string BaseName { get; init; } = "";

    /// <summary>This row's median over the baseline's; <c>NaN</c> when there is no comparison.</summary>
    public required double Ratio { get; init; }

    /// <summary>
    ///     The ratio with an interval on it, estimated by pairing this row's replicates against the
    ///     baseline's. <c>null</c> when the run had fewer than two replicates to pair, in which case
    ///     <see cref="Ratio" /> is the plain quotient of the two medians and carries no interval.
    /// </summary>
    public RatioEstimate? RatioEstimate { get; init; }

    /// <summary>
    ///     Whether <see cref="Ratio" /> is <c>NaN</c> because this row and the baseline were measured
    ///     under different runtime configurations, rather than because there was nothing to compare.
    /// </summary>
    /// <remarks>
    ///     Reporters render these as <c>n/a</c> and say why. The distinction matters: an absent
    ///     ratio on a single-benchmark table means "no comparison exists", while this one means
    ///     "a comparison exists and would have been misleading".
    /// </remarks>
    public bool RatioSuppressed { get; init; }

    /// <summary>The pooled significance verdict rendered for this row; empty when not tested.</summary>
    public string SignificanceLabel { get; init; } = "";

    /// <summary>
    ///     Whether this row is the table's baseline. Not simply
    ///     <see cref="BenchmarkResult.IsBaseline" />: a result that declared itself the baseline is
    ///     only the baseline of a table that actually compares against it, so a non-comparable table
    ///     and a per-parameter partition both re-decide this.
    /// </summary>
    public required bool IsBaseline { get; init; }

    /// <inheritdoc cref="BenchmarkResult.GetPercentile" />
    public double? GetPercentile(double p) => Result.GetPercentile(p);
}
