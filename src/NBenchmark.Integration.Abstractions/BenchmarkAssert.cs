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

        var needsRelaxation = RegressionTolerance.NeedsRelaxation(
            result,
            host.IsSharedRunner,
            AutoTuneOptions.Default.JitterAutoSwitchThreshold);

        var configuredTolerance = thresholds.MaxAbsoluteThresholdTolerance > 0
            ? thresholds.MaxAbsoluteThresholdTolerance
            : 1.0;

        var toleranceMultiplier = needsRelaxation ? configuredTolerance : 1.0;

        if (thresholds.MaxMeanNs.HasValue)
        {
            var verdict = RegressionTolerance.Evaluate(result.Mean, thresholds.MaxMeanNs.Value, toleranceMultiplier);

            if (verdict.ExceedsThreshold)
            {
                var message = $"Mean {result.Mean:F2} ns exceeds maximum {thresholds.MaxMeanNs.Value:F2} ns";

                if (verdict.Relaxed)
                    message += $" (relaxed to {verdict.EffectiveThreshold:F2} ns for shared-runner jitter tolerance)";

                message += $" (excess: {verdict.Excess:F2} ns)";

                violations.Add(message);
            }
        }

        var p95 = result.GetPercentile(0.95);

        if (thresholds.MaxP95Ns.HasValue && p95.HasValue)
        {
            var verdict = RegressionTolerance.Evaluate(p95.Value, thresholds.MaxP95Ns.Value, toleranceMultiplier);

            if (verdict.ExceedsThreshold)
            {
                var message = $"P95 {p95.Value:F2} ns exceeds maximum {thresholds.MaxP95Ns.Value:F2} ns";

                if (verdict.Relaxed)
                    message += $" (relaxed to {verdict.EffectiveThreshold:F2} ns for shared-runner jitter tolerance)";

                message += $" (excess: {verdict.Excess:F2} ns)";

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
            var verdict = RegressionTolerance.Evaluate(
                result.MeanAllocatedBytes.Value,
                thresholds.MaxAllocatedBytes.Value,
                toleranceMultiplier);

            if (verdict.ExceedsThreshold)
            {
                var effectiveMax = verdict.EffectiveThreshold >= long.MaxValue
                    ? long.MaxValue
                    : (long)verdict.EffectiveThreshold;

                var message = $"Mean allocated bytes {result.MeanAllocatedBytes.Value} exceeds maximum " +
                              $"{thresholds.MaxAllocatedBytes.Value}";

                if (verdict.Relaxed)
                    message += $" (relaxed to {effectiveMax} for shared-runner jitter tolerance)";

                message += $" (excess: {result.MeanAllocatedBytes.Value - effectiveMax})";

                violations.Add(message);
            }
        }

        return violations;
    }
}
