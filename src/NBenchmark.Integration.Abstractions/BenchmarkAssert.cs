namespace NBenchmark.Integration.Abstractions;

public static class BenchmarkAssert
{
    public static IReadOnlyList<string> Validate(BenchmarkResult result, PerformanceThresholds thresholds)
    {
        var violations = new List<string>();

        if (thresholds.MaxMeanNs.HasValue && result.Mean > thresholds.MaxMeanNs.Value)
        {
            violations.Add(
                $"Mean {result.Mean:F2} ns exceeds maximum {thresholds.MaxMeanNs.Value:F2} ns " +
                $"(excess: {result.Mean - thresholds.MaxMeanNs.Value:F2} ns)");
        }

        if (thresholds.MaxP95Ns.HasValue && result.P95 > thresholds.MaxP95Ns.Value)
        {
            violations.Add(
                $"P95 {result.P95:F2} ns exceeds maximum {thresholds.MaxP95Ns.Value:F2} ns " +
                $"(excess: {result.P95 - thresholds.MaxP95Ns.Value:F2} ns)");
        }

        if (thresholds.MaxAllocatedBytes.HasValue
            && result.MeanAllocatedBytes.HasValue
            && result.MeanAllocatedBytes.Value > thresholds.MaxAllocatedBytes.Value)
        {
            violations.Add(
                $"Mean allocated bytes {result.MeanAllocatedBytes.Value} exceeds maximum " +
                $"{thresholds.MaxAllocatedBytes.Value} " +
                $"(excess: {result.MeanAllocatedBytes.Value - thresholds.MaxAllocatedBytes.Value})");
        }

        return violations;
    }
}
