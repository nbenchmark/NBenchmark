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

        var p95 = result.GetPercentile(0.95);

        if (thresholds.MaxP95Ns.HasValue && p95.HasValue && p95.Value > thresholds.MaxP95Ns.Value)
        {
            violations.Add(
                $"P95 {p95.Value:F2} ns exceeds maximum {thresholds.MaxP95Ns.Value:F2} ns " +
                $"(excess: {p95.Value - thresholds.MaxP95Ns.Value:F2} ns)");
        }
        else if (thresholds.MaxP95Ns.HasValue && p95 is null)
        {
            violations.Add(
                "P95 threshold specified but P95 was not computed " +
                "(check MeasurementOptions.ReportedPercentiles includes 0.95).");
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
