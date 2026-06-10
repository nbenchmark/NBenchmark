namespace NBenchmark;

public sealed record BenchmarkTable
{
    public required IReadOnlyList<BenchmarkRow> Rows { get; init; }
    public required string RunAtUtc { get; init; }
    public required int WarmupIterations { get; init; }
    public required int MeasuredIterations { get; init; }
    public required double ConfidenceLevel { get; init; }
    public required OutlierMode OutlierMode { get; init; }
    public required TimeSpan TotalDuration { get; init; }

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
            OutlierMode = results.FirstOrDefault()?.OutlierMode ?? OutlierMode.RemoveTop5Percent,
            TotalDuration = results.Aggregate(TimeSpan.Zero, (a, r) => a + r.TotalDuration),
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

        return result.SignificanceVerdict == SignificanceVerdict.Significant ? "✓" : "~";
    }
}

public record BenchmarkRow
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required double Median { get; init; }
    public required double Mean { get; init; }
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
}
