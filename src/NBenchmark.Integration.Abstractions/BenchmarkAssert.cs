using NBenchmark.Engine;

namespace NBenchmark.Integration.Abstractions;

public static class BenchmarkAssert
{
    private static HostAssessment? _cachedHostAssessment;
    private static readonly object HostLock = new();

    internal static HostAssessment GetHostAssessment()
    {
        lock (HostLock)
        {
            if (!_cachedHostAssessment.HasValue)
                _cachedHostAssessment = EnvironmentControl.AssessHost();

            return _cachedHostAssessment.Value;
        }
    }

    public static void ResetHostAssessment()
    {
        lock (HostLock)
        {
            _cachedHostAssessment = null;
        }
    }

    public static void SetHostAssessment(HostAssessment assessment)
    {
        lock (HostLock)
        {
            _cachedHostAssessment = assessment;
        }
    }

    public static IReadOnlyList<string> Validate(BenchmarkResult result, PerformanceThresholds thresholds)
    {
        var violations = new List<string>();

        var host = GetHostAssessment();
        var jitter = result.AutoTune?.JitterMetric;
        var needsRelaxation = host.IsSharedRunner
            || (jitter.HasValue && jitter.Value > AutoTuneOptions.Default.JitterAutoSwitchThreshold);
        var configuredTolerance = thresholds.MaxAbsoluteThresholdTolerance > 0
            ? thresholds.MaxAbsoluteThresholdTolerance
            : 1.0;
        var tolerance = needsRelaxation ? configuredTolerance : 1.0;

        if (thresholds.MaxMeanNs.HasValue)
        {
            var effectiveMax = thresholds.MaxMeanNs.Value * tolerance;

            if (result.Mean > effectiveMax)
            {
                var message = $"Mean {result.Mean:F2} ns exceeds maximum {thresholds.MaxMeanNs.Value:F2} ns";

                if (tolerance > 1.0)
                    message += $" (relaxed to {effectiveMax:F2} ns for shared-runner jitter tolerance)";

                message += $" (excess: {result.Mean - effectiveMax:F2} ns)";

                violations.Add(message);
            }
        }

        var p95 = result.GetPercentile(0.95);

        if (thresholds.MaxP95Ns.HasValue && p95.HasValue)
        {
            var effectiveMax = thresholds.MaxP95Ns.Value * tolerance;

            if (p95.Value > effectiveMax)
            {
                var message = $"P95 {p95.Value:F2} ns exceeds maximum {thresholds.MaxP95Ns.Value:F2} ns";

                if (tolerance > 1.0)
                    message += $" (relaxed to {effectiveMax:F2} ns for shared-runner jitter tolerance)";

                message += $" (excess: {p95.Value - effectiveMax:F2} ns)";

                violations.Add(message);
            }
        }
        else if (thresholds.MaxP95Ns.HasValue && p95 is null)
        {
            violations.Add(
                "P95 threshold specified but P95 was not computed " +
                "(check MeasurementOptions.ReportedPercentiles includes 0.95).");
        }

        if (thresholds.MaxAllocatedBytes.HasValue
            && result.MeanAllocatedBytes.HasValue)
        {
            var effectiveMaxDouble = thresholds.MaxAllocatedBytes.Value * tolerance;
            var effectiveMax = effectiveMaxDouble >= long.MaxValue ? long.MaxValue : (long)effectiveMaxDouble;

            if (result.MeanAllocatedBytes.Value > effectiveMax)
            {
                var message = $"Mean allocated bytes {result.MeanAllocatedBytes.Value} exceeds maximum " +
                              $"{thresholds.MaxAllocatedBytes.Value}";

                if (tolerance > 1.0)
                    message += $" (relaxed to {effectiveMax} for shared-runner jitter tolerance)";

                message += $" (excess: {result.MeanAllocatedBytes.Value - effectiveMax})";

                violations.Add(message);
            }
        }

        return violations;
    }
}
