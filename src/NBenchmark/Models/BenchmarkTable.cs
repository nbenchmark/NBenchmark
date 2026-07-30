using System.Globalization;
using NBenchmark.Engine;
using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

public sealed record BenchmarkTable
{
    /// <summary>
    ///     When set by the harness, <see cref="BuildPerClass" /> returns a single combined table
    ///     with a <see cref="BenchmarkRow.ClassName" /> column instead of one table per class.
    ///     The harness sets this before calling reporters and clears it after.
    /// </summary>
    public static bool CrossClassMode { get; set; }

    public required IReadOnlyList<BenchmarkRow> Rows { get; init; }
    public required string RunAtUtc { get; init; }
    public required int WarmupIterations { get; init; }
    public required int MeasuredIterations { get; init; }
    public required double ConfidenceLevel { get; init; }
    public required string OutlierDetector { get; init; }
    public required TimeSpan TotalDuration { get; init; }
    public double SignificanceLevel { get; init; } = 0.05;

    /// <summary>The display name of the pairwise significance strategy used (e.g. Mann-Whitney U).</summary>
    public string SignificanceTestName { get; init; } = DefaultSignificanceTest.Instance.Name;

    /// <summary>The measurement profile under which the run was produced.</summary>
    public MeasurementProfile Profile { get; init; } = MeasurementProfile.Realistic;

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
    ///     profile - for example a class combining <c>[InProcess]</c> benchmarks with isolated
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
    ///     <c>true</c> when the rows carry more than one <see cref="IsolationStatus" />. Reporters
    ///     add a per-row status column when this is set, because a single table-wide footer cannot
    ///     say <i>which</i> rows were isolated, and that is exactly what a reader needs to know
    ///     before trusting any comparison between them.
    /// </summary>
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
            g.GroupBy(r => r.RuntimeMoniker, StringComparer.Ordinal)
                .Any(runtimeGroup => runtimeGroup.Count(r => !r.Errored) > 1));

        if (!anyGroupComparison)
        {
            foreach (var runtimeGroup in results.GroupBy(r => r.RuntimeMoniker, StringComparer.Ordinal))
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

                foreach (var result in runtimeResults.OrderBy(r => r.Median))
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
            foreach (var runtimeGroup in group.GroupBy(r => r.RuntimeMoniker, StringComparer.Ordinal))
            {
                var runtimeResults = runtimeGroup.ToList();
                var successful = runtimeResults.Where(r => !r.Errored).ToList();
                var baseline = ComparisonGroup.PickBaseline(successful);
                var multiBenchmark = successful.Count > 1;

                foreach (var result in runtimeResults.OrderBy(r => r.Median))
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
            .Select(r => r.RuntimeMoniker)
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

            foreach (var runtimeGroup in results.GroupBy(r => r.RuntimeMoniker, StringComparer.Ordinal))
            {
                var runtimeResults = runtimeGroup.ToList();
                var successful = runtimeResults.Where(r => !r.Errored).ToList();
                var runtimeBaseline = ComparisonGroup.PickBaseline(successful);
                var runtimeMultiBenchmark = runtimeResults.Count > 1;

                foreach (var result in runtimeResults.OrderBy(r => r.Median))
                {
                    rowsByRuntime.Add(BuildRow(result, runtimeBaseline, runtimeMultiBenchmark));
                }
            }

            return AssembleTable(results, rowsByRuntime, []);
        }

        var rows = results
            .OrderBy(r => r.Median)
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
            ParameterNames = parameterNames,
            RunAtUtc = headerSource?.RunAtUtc.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            WarmupIterations = headerSource?.WarmupIterations ?? 0,
            MeasuredIterations = headerSource?.MeasuredIterations ?? 0,
            ConfidenceLevel = headerSource?.ConfidenceLevel ?? 0.95,
            OutlierDetector = results.FirstOrDefault()?.OutlierDetector ?? OutlierDetectors.IqrFence.Name,
            TotalDuration = results.Aggregate(TimeSpan.Zero, (a, r) => a + r.TotalDuration),
            SignificanceLevel = headerSource?.SignificanceLevel ?? 0.05,
            SignificanceTestName = headerSource?.SignificanceTestName ?? DefaultSignificanceTest.Instance.Name,
            Profile = results.FirstOrDefault()?.Profile ?? MeasurementProfile.Realistic,
            RuntimeProfileName = results.FirstOrDefault()?.RuntimeProfileName ?? RuntimeProfile.Host.Name,
            RuntimeKnobs = results.FirstOrDefault()?.RuntimeKnobs ?? "",
            MixedRuntimeProfiles = results
                .Select(r => r.RuntimeProfileName)
                .Distinct(StringComparer.Ordinal)
                .Count() > 1,
            InProcessReasons = results
                .Where(r => !r.IsolationStatus.IsIsolated())
                .Select(r => r.IsolationStatus)
                .Distinct()
                .OrderBy(s => s)
                .ToList(),
            MixedIsolationStatuses = results
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
            Name = result.Name,
            ClassName = result.ClassName,
            Description = result.Description,
            Median = result.Median,
            Mean = result.Mean,
            OperationsPerSecond = result.OperationsPerSecond,
            MedianOperationsPerSecond = result.MedianOperationsPerSecond,
            MarginOfError = result.MarginOfError,
            StandardDeviation = result.StandardDeviation,
            StandardError = result.StandardError,
            CoefficientOfVariation = result.CoefficientOfVariation,
            Percentiles = result.Percentiles,
            Histogram = result.Histogram,
            RawSamples = result.RawSamples,
            TrimmedOrdinals = result.TrimmedOrdinals,
            Ratio = ratio?.Value ?? (comparable && !mixedGroup ? ComputeRatio(result, baseline) : double.NaN),
            RatioEstimate = ratio,
            RatioSuppressed = mixedGroup,
            IsolationStatus = result.IsolationStatus,
            RuntimeProfileName = result.RuntimeProfileName,
            IsBaseline = comparable && (isBaselineOverride ?? result.IsBaseline),
            Errored = result.Errored,
            ErrorMessage = result.ErrorMessage,
            MeanAllocatedBytes = result.MeanAllocatedBytes,
            ConfidenceIntervalLower = result.ConfidenceIntervalLower,
            ConfidenceIntervalUpper = result.ConfidenceIntervalUpper,
            SignificanceLabel = ComputeSignificanceLabel(result, multiBenchmark),
            Effect = result.Effect,
            Warnings = result.Warnings,
            Q1 = result.Q1,
            Q3 = result.Q3,
            InterquartileRange = result.InterquartileRange,
            LowerFence = result.LowerFence,
            UpperFence = result.UpperFence,
            OutliersRemoved = result.OutliersRemoved,
            N = result.N,
            Skewness = result.Skewness,
            Kurtosis = result.Kurtosis,
            Mad = result.Mad,
            MedianCiLower = result.MedianCiLower,
            MedianCiUpper = result.MedianCiUpper,
            MedianShift = result.MedianShift,
            AllocMedian = result.AllocMedian,
            AllocP95 = result.AllocP95,
            AllocMax = result.AllocMax,
            Range = result.Range,
            Min = result.Min,
            Max = result.Max,
            WarmupIterations = result.WarmupIterations,
            ConfidenceLevel = result.ConfidenceLevel,
            StandardErrorPercent = result.StandardErrorPercent,
            MarginPercent = result.MarginPercent,
            CoefficientOfVariationPercent = result.CoefficientOfVariationPercent,
            AutoTune = result.AutoTune,
            LaunchStatistics = result.LaunchStatistics,
            Diagnostics = result.Diagnostics,
            Categories = result.Categories,
            ParameterSet = result.ParameterSet,
            BaseName = ComputeBaseName(result),
            RuntimeMoniker = result.RuntimeMoniker,
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
        if (result.Errored || baseline is null || baseline.Median == 0)
            return double.NaN;

        return result.Median / baseline.Median;
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

        lines.Add($"Iterations: {row.N} measured (warmup: {row.WarmupIterations}, pre-trim: {row.N + row.OutliersRemoved})");

        if (row.OutliersRemoved > 0)
        {
            var label = row.OutliersRemoved == 1 ? "outlier" : "outliers";
            lines.Add($"Outliers: {row.OutliersRemoved} {label} removed");
        }

        lines.Add($"Range: {BenchmarkFormatter.FormatNs(row.Range)} ({BenchmarkFormatter.FormatNs(row.Min)} -> {BenchmarkFormatter.FormatNs(row.Max)})");

        lines.Add(
            $"Quartiles: Q1 = {BenchmarkFormatter.FormatNs(row.Q1)}, Q3 = {BenchmarkFormatter.FormatNs(row.Q3)}, IQR = {BenchmarkFormatter.FormatNs(row.InterquartileRange)}");

        if (row.LowerFence is not null && row.UpperFence is not null)
            lines.Add($"Fences: [{BenchmarkFormatter.FormatNs(row.LowerFence.Value)}; {BenchmarkFormatter.FormatNs(row.UpperFence.Value)}]");

        lines.Add(
            $"CI: [{BenchmarkFormatter.FormatNs(row.ConfidenceIntervalLower)}; {BenchmarkFormatter.FormatNs(row.ConfidenceIntervalUpper)}] (CI {row.ConfidenceLevel * 100:F1}%)");

        if (row.MedianCiLower is { } medianLower && row.MedianCiUpper is { } medianUpper)
        {
            lines.Add(
                $"Median CI: [{BenchmarkFormatter.FormatNs(medianLower)}; {BenchmarkFormatter.FormatNs(medianUpper)}] "
                + $"(distribution-free, CI {row.ConfidenceLevel * 100:F1}%)");
        }

        lines.Add($"Margin: ±{BenchmarkFormatter.FormatNs(row.MarginOfError)} ({row.MarginPercent:F2}% of Mean)");

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

        lines.Add($"CV: {row.CoefficientOfVariation:F4} ({row.CoefficientOfVariationPercent:F2}%)");

        var skewSuffix = row.N < 3 ? " (n too small)" : "";
        lines.Add($"Skewness: {row.Skewness:F4}{skewSuffix}");

        var kurtSuffix = row.N < 4 ? " (n too small)" : "";
        lines.Add($"Kurtosis: {row.Kurtosis:F4}{kurtSuffix}");

        lines.Add($"MAD: {BenchmarkFormatter.FormatNs(row.Mad)}");

        if (row.Percentiles.Count > 0)
        {
            var parts = row.Percentiles.Select(e =>
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

        if (row.Effect is { } effect && string.Equals(effect.Metric, EffectMetrics.CliffsDelta, StringComparison.Ordinal))
        {
            var magnitudeText = effect.Magnitude ?? "?";
            var deltaText = effect.Value.HasValue ? effect.Value.Value.ToString("F4") : "?";

            var directionText = effect.Direction switch
            {
                EffectDirection.CandidateHigher => "slower than baseline",
                EffectDirection.CandidateLower => "faster than baseline",
                _ => "similar to baseline",
            };

            lines.Add($"Cliff's \u03b4: {deltaText} ({magnitudeText}) \u2014 candidate tends to be {directionText}");
        }
        else if (row.Effect is { } genericEffect)
        {
            var valueText = genericEffect.Value.HasValue ? genericEffect.Value.Value.ToString("F4") : "?";
            var magnitudeText = string.IsNullOrWhiteSpace(genericEffect.Magnitude) ? "?" : genericEffect.Magnitude;
            lines.Add($"Effect ({genericEffect.Metric}): {valueText} ({magnitudeText})");
        }

        if (row.MedianShift is { } shift)
            lines.Add($"Median shift (Hodges-Lehmann): {FormatShift(shift)}");

        if (row.AllocMedian is not null)
        {
            lines.Add("");
            lines.Add("Allocations:");
            lines.Add($"  Mean: {BenchmarkFormatter.FormatAlloc(row.MeanAllocatedBytes ?? 0)}");
            lines.Add($"  P50:  {BenchmarkFormatter.FormatAlloc(row.AllocMedian.Value)}");
            lines.Add($"  P95:  {BenchmarkFormatter.FormatAlloc(row.AllocP95 ?? 0)}");
            lines.Add($"  Max:  {BenchmarkFormatter.FormatAlloc(row.AllocMax ?? 0)}");
        }

        if (row.Diagnostics is { } diag)
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

public record BenchmarkRow
{
    public required string Name { get; init; }
    public string ClassName { get; init; } = "";

    /// <summary>
    ///     The benchmark name with any parameter suffix removed (e.g. <c>Sort</c> for
    ///     <c>Sort(size=10)</c>). Equal to <see cref="Name" /> for non-parameterised benchmarks.
    ///     Reporters show this in the Benchmark column while parameter values appear as columns.
    /// </summary>
    public string BaseName { get; init; } = "";

    /// <summary>The parameter set for this row; empty for non-parameterised benchmarks.</summary>
    public IReadOnlyList<BenchmarkParameter> ParameterSet { get; init; } = [];

    public string? Description { get; init; }
    public required double Median { get; init; }
    public required double Mean { get; init; }
    public required double OperationsPerSecond { get; init; }
    public required double MedianOperationsPerSecond { get; init; }
    public required double MarginOfError { get; init; }
    public required double StandardDeviation { get; init; }
    public required double StandardError { get; init; }
    public required double CoefficientOfVariation { get; init; }
    public required IReadOnlyList<PercentileEntry> Percentiles { get; init; }
    public LatencyHistogram? Histogram { get; init; }

    /// <summary>
    ///     The raw per-op nanoseconds of every measured sample, in sample order, before outlier
    ///     trimming. Empty for dry-run, errored, or calibration-derived results. Used by the
    ///     Console reporter's density sparkline.
    /// </summary>
    public IReadOnlyList<double> RawSamples { get; init; } = [];

    /// <summary>
    ///     Ordinals (zero-based positions in <see cref="RawSamples" />) of every sample the
    ///     outlier detector discarded. Used by the Console reporter to mark trimmed samples.
    /// </summary>
    public IReadOnlyList<int> TrimmedOrdinals { get; init; } = [];

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

    /// <summary>Where this row was measured, and if it was not isolated, why not.</summary>
    public IsolationStatus IsolationStatus { get; init; } = IsolationStatus.InProcessRequested;

    /// <summary>The runtime profile the measuring process was launched under.</summary>
    public string RuntimeProfileName { get; init; } = RuntimeProfile.Host.Name;

    public required bool IsBaseline { get; init; }
    public required bool Errored { get; init; }
    public string? ErrorMessage { get; init; }
    public required double ConfidenceIntervalLower { get; init; }
    public required double ConfidenceIntervalUpper { get; init; }
    public long? MeanAllocatedBytes { get; init; }
    public string SignificanceLabel { get; init; } = "";
    public EffectSize? Effect { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    ///     Cross-launch summary when the benchmark was run with
    ///     <see cref="MeasurementOptions.LaunchCount" /> > 1. <c>null</c> for
    ///     single-launch runs. Reporters can display this to explain
    ///     between-launch variance.
    /// </summary>
    public LaunchStatistics? LaunchStatistics { get; init; }

    public required double Q1 { get; init; }
    public required double Q3 { get; init; }
    public required double InterquartileRange { get; init; }
    public double? LowerFence { get; init; }
    public double? UpperFence { get; init; }
    public required int OutliersRemoved { get; init; }
    public required int N { get; init; }
    public required double Skewness { get; init; }
    public required double Kurtosis { get; init; }
    public required double Mad { get; init; }

    /// <summary>Lower/upper bounds of the distribution-free median confidence interval; <c>null</c> when undefined.</summary>
    public double? MedianCiLower { get; init; }

    public double? MedianCiUpper { get; init; }

    /// <summary>The Hodges-Lehmann shift versus the baseline with its CI; <c>null</c> for the baseline or when not tested.</summary>
    public ShiftEstimate? MedianShift { get; init; }

    public long? AllocMedian { get; init; }
    public long? AllocP95 { get; init; }
    public long? AllocMax { get; init; }

    public required double Range { get; init; }
    public required double Min { get; init; }
    public required double Max { get; init; }
    public int WarmupIterations { get; init; }
    public double ConfidenceLevel { get; init; }
    public required double StandardErrorPercent { get; init; }
    public required double MarginPercent { get; init; }
    public required double CoefficientOfVariationPercent { get; init; }
    public AutoTuneDiagnostic? AutoTune { get; init; }
    public DiagnosticsResult? Diagnostics { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>
    ///     The target framework moniker (e.g. "net8.0", "net9.0") under which this
    ///     benchmark was executed. Empty for single-runtime runs.
    /// </summary>
    public string RuntimeMoniker { get; init; } = "";

    public double? GetPercentile(double p)
    {
        foreach (var e in Percentiles)
        {
            if (Math.Abs(e.Percentile - p) < 1e-9)
                return e.Value;
        }

        return null;
    }
}
