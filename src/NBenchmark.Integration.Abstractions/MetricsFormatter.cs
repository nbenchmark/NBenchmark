namespace NBenchmark.Integration.Abstractions;

public static class MetricsFormatter
{
    public static string Format(BenchmarkResult result)
    {
        var allocations = result.MeanAllocatedBytes.HasValue
            ? $"{result.MeanAllocatedBytes.Value} B"
            : "n/a";

        return
            $"NBenchmark metrics{Environment.NewLine}" +
            $"Mean: {result.Mean:F2} ns{Environment.NewLine}" +
            $"P95: {result.P95:F2} ns{Environment.NewLine}" +
            $"Allocations: {allocations}{Environment.NewLine}" +
            $"Iterations: {result.MeasuredIterations} (warmup: {result.WarmupIterations})";
    }
}
