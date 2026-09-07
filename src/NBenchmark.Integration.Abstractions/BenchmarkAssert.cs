using NBenchmark.Engine;

namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     Validates a <see cref="BenchmarkResult" /> against a set of <see cref="PerformanceThresholds" />,
///     for the <c>PerformanceAssert</c> pattern where the caller has already measured and just wants
///     the pass/fail decision. Relaxes absolute thresholds on a shared, jittery runner the same way
///     the attribute-driven gates do.
/// </summary>
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

    /// <summary>
    ///     Clears the cached host assessment so the next gate re-measures the machine. A test seam:
    ///     nothing in a consumer's test run wants a stale reading, and nothing but a test wants to
    ///     control when the reading is taken.
    /// </summary>
    internal static void ResetHostAssessment()
    {
        lock (HostLock)
        {
            _cachedHostAssessment = null;
        }
    }

    /// <summary>
    ///     Pins the host assessment the gates run against. The counterpart of
    ///     <see cref="ResetHostAssessment" />, and internal for the same reason - it also takes
    ///     <see cref="HostAssessment" />, which is engine detail.
    /// </summary>
    internal static void SetHostAssessment(HostAssessment assessment)
    {
        lock (HostLock)
        {
            _cachedHostAssessment = assessment;
        }
    }

    /// <summary>
    ///     Checks <paramref name="result" /> against <paramref name="thresholds" /> and returns a
    ///     human-readable message for every threshold it exceeds (mean, median, P95, and allocated
    ///     bytes), relaxed for shared-runner jitter where <see cref="RegressionTolerance" /> applies.
    ///     An empty list means every configured threshold was met.
    /// </summary>
    /// <param name="result">The measurement to check.</param>
    /// <param name="thresholds">The limits to check it against; an unset threshold is not checked.</param>
    /// <returns>The violation messages, or an empty list if <paramref name="result" /> passes.</returns>
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
            var verdict = RegressionTolerance.Evaluate(result.MeanNs, thresholds.MaxMeanNs.Value, toleranceMultiplier);

            if (verdict.ExceedsThreshold)
            {
                var message = $"MeanNs {result.MeanNs:F2} ns exceeds maximum {thresholds.MaxMeanNs.Value:F2} ns";

                if (verdict.Relaxed)
                    message += $" (relaxed to {verdict.EffectiveThreshold:F2} ns for shared-runner jitter tolerance)";

                message += $" (excess: {verdict.Excess:F2} ns)";

                violations.Add(message);
            }
        }

        if (thresholds.MaxMedianNs.HasValue)
        {
            var verdict = RegressionTolerance.Evaluate(
                result.MedianNs, thresholds.MaxMedianNs.Value, toleranceMultiplier);

            if (verdict.ExceedsThreshold)
            {
                var message = $"MedianNs {result.MedianNs:F2} ns exceeds maximum {thresholds.MaxMedianNs.Value:F2} ns";

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
            && result.AllocatedBytesMean.HasValue)
        {
            var verdict = RegressionTolerance.Evaluate(
                result.AllocatedBytesMean.Value,
                thresholds.MaxAllocatedBytes.Value,
                toleranceMultiplier);

            if (verdict.ExceedsThreshold)
            {
                var effectiveMax = verdict.EffectiveThreshold >= long.MaxValue
                    ? long.MaxValue
                    : (long)verdict.EffectiveThreshold;

                var message = $"Mean allocated bytes {result.AllocatedBytesMean.Value} exceeds maximum " +
                              $"{thresholds.MaxAllocatedBytes.Value}";

                if (verdict.Relaxed)
                    message += $" (relaxed to {effectiveMax} for shared-runner jitter tolerance)";

                message += $" (excess: {result.AllocatedBytesMean.Value - effectiveMax})";

                violations.Add(message);
            }
        }

        return violations;
    }
}
