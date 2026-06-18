using NBenchmark.Reporters;
using NBenchmark.Stats;

namespace NBenchmark;

public sealed record BenchmarkTable
{
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
    ///     The omnibus significance verdict (e.g. Kruskal-Wallis) across all benchmarks, when
    ///     an omnibus test was run (three or more groups); otherwise <c>null</c>.
    /// </summary>
    public OmnibusComparison? Omnibus { get; init; }

    public static BenchmarkTable Build(IReadOnlyList<BenchmarkResult> results)
    {
        var successful = results.Where(r => !r.Errored).ToList();
        var multiBenchmark = results.Count > 1;

        BenchmarkResult? baseline = null;

        if (successful.Count > 0)
            baseline = successful.FirstOrDefault(r => r.IsBaseline) ?? successful.MinBy(r => r.Median);

        var headerSource = successful.Count > 0 ? successful[0] : null;

        var rows = results
            .OrderBy(r => r.Median)
            .Select(r => BuildRow(r, baseline, multiBenchmark))
            .ToList();

        return new BenchmarkTable
        {
            Rows = rows,
            RunAtUtc = headerSource?.RunAtUtc.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
            WarmupIterations = headerSource?.WarmupIterations ?? 0,
            MeasuredIterations = headerSource?.MeasuredIterations ?? 0,
            ConfidenceLevel = headerSource?.ConfidenceLevel ?? 0.95,
            OutlierDetector = results.FirstOrDefault()?.OutlierDetector ?? OutlierDetectors.IqrFence.Name,
            TotalDuration = results.Aggregate(TimeSpan.Zero, (a, r) => a + r.TotalDuration),
            SignificanceLevel = headerSource?.SignificanceLevel ?? 0.05,
            SignificanceTestName = headerSource?.SignificanceTestName ?? DefaultSignificanceTest.Instance.Name,
            Profile = results.FirstOrDefault()?.Profile ?? MeasurementProfile.Realistic,
            Omnibus = results.FirstOrDefault(r => r.Omnibus is not null)?.Omnibus,
        };
    }

    private static BenchmarkRow BuildRow(BenchmarkResult result, BenchmarkResult? baseline, bool multiBenchmark)
    {
        return new BenchmarkRow
        {
            Name = result.Name,
            Description = result.Description,
            Median = result.Median,
            Mean = result.Mean,
            OperationsPerSecond = result.OperationsPerSecond,
            MedianOperationsPerSecond = result.MedianOperationsPerSecond,
            MarginOfError = result.MarginOfError,
            StandardDeviation = result.StandardDeviation,
            StandardError = result.StandardError,
            CoefficientOfVariation = result.CoefficientOfVariation,
            P95 = result.P95,
            P99 = result.P99,
            Ratio = ComputeRatio(result, baseline),
            IsBaseline = result.IsBaseline,
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
            Categories = result.Categories,
        };
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
        if (detail == ReportDetail.Simple)
            return "";

        var lines = new List<string>();

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

        lines.Add($"Iterations: {row.N} measured (warmup: {row.WarmupIterations}, pre-trim: {row.N + row.OutliersRemoved})");

        lines.Add(
            $"CI: [{BenchmarkFormatter.FormatNs(row.ConfidenceIntervalLower)}; {BenchmarkFormatter.FormatNs(row.ConfidenceIntervalUpper)}] (CI {row.ConfidenceLevel * 100:F1}%)");

        lines.Add($"Margin: ±{BenchmarkFormatter.FormatNs(row.MarginOfError)} ({row.MarginPercent:F2}% of Mean)");

        lines.Add($"CV: {row.CoefficientOfVariation:F4} ({row.CoefficientOfVariationPercent:F2}%)");

        var skewSuffix = row.N < 3 ? " (n too small)" : "";
        lines.Add($"Skewness: {row.Skewness:F4}{skewSuffix}");

        var kurtSuffix = row.N < 4 ? " (n too small)" : "";
        lines.Add($"Kurtosis: {row.Kurtosis:F4}{kurtSuffix}");

        lines.Add($"MAD: {BenchmarkFormatter.FormatNs(row.Mad)}");

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

        lines.Add($"N: {row.N} samples");

        if (row.AllocMedian is not null)
        {
            lines.Add("");
            lines.Add("Allocations:");
            lines.Add($"  Mean: {BenchmarkFormatter.FormatAlloc(row.MeanAllocatedBytes ?? 0)}");
            lines.Add($"  P50:  {BenchmarkFormatter.FormatAlloc(row.AllocMedian.Value)}");
            lines.Add($"  P95:  {BenchmarkFormatter.FormatAlloc(row.AllocP95 ?? 0)}");
            lines.Add($"  Max:  {BenchmarkFormatter.FormatAlloc(row.AllocMax ?? 0)}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    ///     Formats the adaptive measurement diagnostic into a single human-readable summary line
    ///     (e.g. <c>auto-tuned: 240 samples × 64 ops, warmup 40, CI ±1.8%</c>).
    /// </summary>
    public static string FormatAutoTuneSummary(AutoTuneDiagnostic diagnostic)
    {
        return $"auto-tuned: {diagnostic.ResolvedSamples:N0} samples × {diagnostic.OpsPerSample:N0} ops, "
               + $"warmup {diagnostic.ResolvedWarmup:N0}, CI ±{diagnostic.AchievedRelativeCiWidth * 100:F1}%";
    }
}

    public record BenchmarkRow
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required double Median { get; init; }
        public required double Mean { get; init; }
        public required double OperationsPerSecond { get; init; }
        public required double MedianOperationsPerSecond { get; init; }
        public required double MarginOfError { get; init; }
        public required double StandardDeviation { get; init; }
        public required double StandardError { get; init; }
        public required double CoefficientOfVariation { get; init; }
        public required double P95 { get; init; }
        public required double P99 { get; init; }
        public required double Ratio { get; init; }
        public required bool IsBaseline { get; init; }
        public required bool Errored { get; init; }
        public string? ErrorMessage { get; init; }
        public required double ConfidenceIntervalLower { get; init; }
        public required double ConfidenceIntervalUpper { get; init; }
        public long? MeanAllocatedBytes { get; init; }
        public string SignificanceLabel { get; init; } = "";
        public EffectSize? Effect { get; init; }
        public IReadOnlyList<string> Warnings { get; init; } = [];

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
        public IReadOnlyList<string> Categories { get; init; } = [];
    }

