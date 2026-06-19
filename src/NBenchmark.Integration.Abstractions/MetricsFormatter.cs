namespace NBenchmark.Integration.Abstractions;

public static class MetricsFormatter
{
    public static string Format(BenchmarkResult result)
    {
        var allocations = result.MeanAllocatedBytes.HasValue
            ? $"{result.MeanAllocatedBytes.Value} B"
            : "n/a";

        var p95 = result.GetPercentile(0.95);
        var p95Text = p95.HasValue ? $"{p95.Value:F2} ns" : "n/a";

        return
            $"NBenchmark metrics{Environment.NewLine}" +
            $"Mean: {result.Mean:F2} ns{Environment.NewLine}" +
            $"P95: {p95Text}{Environment.NewLine}" +
            $"Allocations: {allocations}{Environment.NewLine}" +
            $"Iterations: {result.MeasuredIterations} (warmup: {result.WarmupIterations})";
    }
}
