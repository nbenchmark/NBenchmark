using NBenchmark.Stats;

namespace NBenchmark.Integration.Abstractions;

public static class RelativeComparison
{
    public static IReadOnlyList<string> Check(
        BenchmarkResult candidateResult,
        double[] candidateSamples,
        BenchmarkResult referenceResult,
        double[] referenceSamples,
        double maxSlowdownRatio,
        double significanceLevel = 0.05)
    {
        if (candidateResult.Errored)
            return [];

        var violations = new List<string>();

        if (referenceResult.Errored)
        {
            violations.Add(
                $"Reference method '{referenceResult.Name}' errored: {referenceResult.ErrorMessage}; cannot compare.");
            return violations;
        }

        if (candidateSamples is null || candidateSamples.Length == 0)
        {
            violations.Add(
                "Current run produced no raw samples; cannot run significance test. " +
                "Ensure the benchmark completed successfully with measurement iterations > 0.");
            return violations;
        }

        if (referenceSamples is null || referenceSamples.Length == 0)
        {
            violations.Add(
                "Reference produced no raw samples; cannot run significance test. " +
                "Ensure the reference benchmark completed successfully.");
            return violations;
        }

        if (referenceResult.Mean <= 0)
        {
            if (candidateResult.Mean > 0)
            {
                violations.Add(
                    $"Regression detected: mean {candidateResult.Mean:F2} ns exceeds non-positive reference {referenceResult.Mean:F2} ns.");
            }

            return violations;
        }

        var mwu = MannWhitneyU.Test(referenceSamples, candidateSamples);
        var statisticallySignificant = !double.IsNaN(mwu.PValue) && mwu.PValue < significanceLevel;
        var ratio = candidateResult.Mean / referenceResult.Mean;
        var practicallySignificant = ratio > maxSlowdownRatio;

        var referenceName = string.IsNullOrEmpty(referenceResult.Name) ? "reference" : referenceResult.Name;

        if (statisticallySignificant && practicallySignificant)
        {
            violations.Add(
                $"Regression detected: mean {candidateResult.Mean:F2} ns vs reference '{referenceName}' {referenceResult.Mean:F2} ns " +
                $"(ratio {ratio:F2}x, p={mwu.PValue:F4}, Cliff's delta={mwu.CliffsDelta:F3}). " +
                $"Significant slowdown exceeding {maxSlowdownRatio:F2}x ratio gate.");
        }

        return violations;
    }
}