using NBenchmark.Stats;

namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     Pairwise regression comparison between a candidate and a reference benchmark.
///     Combines a statistical significance test (Mann-Whitney U) with a practical-effect
///     gate (a maximum slowdown ratio) and reports violations as either structured
///     verdicts or human-readable strings.
/// </summary>
/// <remarks>
///     <para>
///         This is the test-integration-friendly pairwise comparator. For the engine's
///         own multi-benchmark regression gate (which picks a baseline by
///         <see cref="BenchmarkResult.IsBaseline" /> or fastest-by-median and compares
///         every candidate against it by median), see
///         <see cref="Engine.ThresholdCheck" />. Both APIs are public and return
///         structured results; Studio can consume either without reimplementing the
///         baseline-selection or ratio-gate logic.
///     </para>
///     <para>
///         <see cref="Check" /> preserves its string-returning signature for backward
///         compatibility with the existing test-framework adapters; it delegates to
///         <see cref="CheckStructured" /> and formats the structured verdict. New
///         callers (including Studio) should prefer <see cref="CheckStructured" />.
///     </para>
/// </remarks>
public static class RelativeComparison
{
    /// <summary>
    ///     Compares <paramref name="candidateResult" /> against
    ///     <paramref name="referenceResult" /> and returns a list of human-readable
    ///     violation strings. Delegates to <see cref="CheckStructured" /> for the
    ///     actual computation; this overload formats the structured verdict.
    /// </summary>
    public static IReadOnlyList<string> Check(
        BenchmarkResult candidateResult,
        double[] candidateSamples,
        BenchmarkResult referenceResult,
        double[] referenceSamples,
        double maxSlowdownRatio,
        double significanceLevel = 0.05)
    {
        var verdict = CheckStructured(
            candidateResult, candidateSamples, referenceResult, referenceSamples,
            maxSlowdownRatio, significanceLevel);

        return verdict.Violations;
    }

    /// <summary>
    ///     Compares <paramref name="candidateResult" /> against
    ///     <paramref name="referenceResult" /> and returns a structured
    ///     <see cref="RelativeComparisonVerdict" /> carrying the ratio, p-value,
    ///     Cliff's delta, and the violation strings. Use this overload when the caller
    ///     needs the numeric values (for example to build a regression-alert UI) rather
    ///     than just the formatted messages.
    /// </summary>
    public static RelativeComparisonVerdict CheckStructured(
        BenchmarkResult candidateResult,
        double[] candidateSamples,
        BenchmarkResult referenceResult,
        double[] referenceSamples,
        double maxSlowdownRatio,
        double significanceLevel = 0.05)
    {
        var violations = new List<string>();

        if (candidateResult.Errored)
            return new RelativeComparisonVerdict([], double.NaN, double.NaN, double.NaN, IsRegression: false);

        if (referenceResult.Errored)
        {
            violations.Add(
                $"Reference method '{referenceResult.Name}' errored: {referenceResult.ErrorMessage}; cannot compare.");
            return new RelativeComparisonVerdict(violations, double.NaN, double.NaN, double.NaN, IsRegression: false);
        }

        if (candidateSamples is null || candidateSamples.Length == 0)
        {
            violations.Add(
                "Current run produced no raw samples; cannot run significance test. " +
                "Ensure the benchmark completed successfully with measurement iterations > 0.");
            return new RelativeComparisonVerdict(violations, double.NaN, double.NaN, double.NaN, IsRegression: false);
        }

        if (referenceSamples is null || referenceSamples.Length == 0)
        {
            violations.Add(
                "Reference produced no raw samples; cannot run significance test. " +
                "Ensure the reference benchmark completed successfully.");
            return new RelativeComparisonVerdict(violations, double.NaN, double.NaN, double.NaN, IsRegression: false);
        }

        if (referenceResult.Mean <= 0)
        {
            if (candidateResult.Mean > 0)
            {
                violations.Add(
                    $"Regression detected: mean {candidateResult.Mean:F2} ns exceeds non-positive reference {referenceResult.Mean:F2} ns.");
            }

            return new RelativeComparisonVerdict(violations, double.NaN, double.NaN, double.NaN, IsRegression: violations.Count > 0);
        }

        var mwu = MannWhitneyU.Test(referenceSamples, candidateSamples);
        var statisticallySignificant = !double.IsNaN(mwu.PValue) && mwu.PValue < significanceLevel;
        var ratio = candidateResult.Mean / referenceResult.Mean;
        var practicallySignificant = ratio > maxSlowdownRatio;
        var isRegression = statisticallySignificant && practicallySignificant;

        var referenceName = string.IsNullOrEmpty(referenceResult.Name) ? "reference" : referenceResult.Name;

        if (isRegression)
        {
            violations.Add(
                $"Regression detected: mean {candidateResult.Mean:F2} ns vs reference '{referenceName}' {referenceResult.Mean:F2} ns " +
                $"(ratio {ratio:F2}x, p={mwu.PValue:F4}, Cliff's delta={mwu.CliffsDelta:F3}). " +
                $"Significant slowdown exceeding {maxSlowdownRatio:F2}x ratio gate.");
        }

        return new RelativeComparisonVerdict(violations, ratio, mwu.PValue, mwu.CliffsDelta, isRegression);
    }
}

/// <summary>
///     A structured pairwise regression verdict: the candidate/reference ratio, the
///     Mann-Whitney p-value, Cliff's delta, whether the comparison flagged a regression,
///     and the human-readable violation strings (empty when the comparison passed).
/// </summary>
/// <param name="Violations">Human-readable violation strings; empty when the comparison passed.</param>
/// <param name="Ratio">Candidate mean divided by reference mean; <c>NaN</c> when undefined.</param>
/// <param name="PValue">The Mann-Whitney U p-value; <c>NaN</c> when the test could not run.</param>
/// <param name="CliffsDelta">Cliff's delta effect size; <c>NaN</c> when the test could not run.</param>
/// <param name="IsRegression"><c>true</c> when the slowdown is both statistically significant and practically significant (exceeds the ratio gate).</param>
public sealed record RelativeComparisonVerdict(
    IReadOnlyList<string> Violations,
    double Ratio,
    double PValue,
    double CliffsDelta,
    bool IsRegression);