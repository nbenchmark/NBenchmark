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
internal static class RelativeComparison
{
    /// <summary>
    ///     Compares <paramref name="candidateResult" /> against
    ///     <paramref name="referenceResult" /> and returns a list of human-readable
    ///     violation strings. Delegates to <see cref="CheckStructured" /> for the
    ///     actual computation; this overload formats the structured verdict.
    /// </summary>
    public static IReadOnlyList<string> Check(
        BenchmarkResult candidateResult,
        IReadOnlyList<double> candidateSamples,
        BenchmarkResult referenceResult,
        IReadOnlyList<double> referenceSamples,
        double maxSlowdownRatio,
        double significanceLevel = 0.05,
        RatioEstimate? pairedRatio = null)
    {
        var verdict = CheckStructured(
            candidateResult, candidateSamples, referenceResult, referenceSamples,
            maxSlowdownRatio, significanceLevel, pairedRatio);

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
    /// <param name="pairedRatio">
    ///     The paired per-replicate ratio between the two, when they were measured co-resident in two
    ///     or more replicate workers. When present it <b>replaces both halves of the gate's test</b>:
    ///     the ratio compared against <paramref name="maxSlowdownRatio" /> is this estimate rather than
    ///     the quotient of means, and the difference counts as real only when the interval excludes
    ///     <c>1.00x</c> rather than when the Mann-Whitney p-value clears
    ///     <paramref name="significanceLevel" />.
    ///     <para>
    ///         The p-value is still computed and reported, but it is not what the gate turns on. It is
    ///         calculated from samples pooled across replicates, where a large count grants power
    ///         regardless of reproducibility - on bodies of provably identical cost that combination marks
    ///         one significantly slower than another routinely. The interval over per-replicate ratios is
    ///         the run-to-run spread, and it is the quantity a gate that must survive a re-run needs.
    ///     </para>
    ///     <para>
    ///         <c>null</c> keeps the pooled-sample test and the quotient of means, which is all a
    ///         single-replicate measurement can support.
    ///     </para>
    /// </param>
    public static RelativeComparisonVerdict CheckStructured(
        BenchmarkResult candidateResult,
        IReadOnlyList<double> candidateSamples,
        BenchmarkResult referenceResult,
        IReadOnlyList<double> referenceSamples,
        double maxSlowdownRatio,
        double significanceLevel = 0.05,
        RatioEstimate? pairedRatio = null)
    {
        var violations = new List<string>();

        if (candidateResult.Errored)
            return new RelativeComparisonVerdict([], double.NaN, double.NaN, double.NaN, false, pairedRatio);

        if (referenceResult.Errored)
        {
            violations.Add(
                $"Reference method '{referenceResult.Name}' errored: {referenceResult.ErrorMessage}; cannot compare.");

            return new RelativeComparisonVerdict(violations, double.NaN, double.NaN, double.NaN, false, pairedRatio);
        }

        if (candidateSamples is null || candidateSamples.Count == 0)
        {
            violations.Add(
                "Current run produced no raw samples; cannot run significance test. " +
                "Ensure the benchmark completed successfully with measurement samples > 0.");

            return new RelativeComparisonVerdict(violations, double.NaN, double.NaN, double.NaN, false, pairedRatio);
        }

        if (referenceSamples is null || referenceSamples.Count == 0)
        {
            violations.Add(
                "Reference produced no raw samples; cannot run significance test. " +
                "Ensure the reference benchmark completed successfully.");

            return new RelativeComparisonVerdict(violations, double.NaN, double.NaN, double.NaN, false, pairedRatio);
        }

        if (referenceResult.MeanNs <= 0)
        {
            if (candidateResult.MeanNs > 0)
            {
                violations.Add(
                    $"Regression detected: mean {candidateResult.MeanNs:F2} ns exceeds non-positive reference {referenceResult.MeanNs:F2} ns.");
            }

            return new RelativeComparisonVerdict(
                violations, double.NaN, double.NaN, double.NaN, violations.Count > 0, pairedRatio);
        }

        // The samples arrive as read-only lists - the caller's own buffers, which this comparison
        // must not reorder - so hand the test a span over an array it owns when they are not
        // already arrays.
        var mwu = MannWhitneyU.Test(AsSpan(referenceSamples), AsSpan(candidateSamples));

        // The paired estimate when the measurement produced one, on both counts: the ratio the gate
        // applies its threshold to, and the test of whether the two differ at all. See the parameter
        // documentation for why the pooled p-value is reported but not gated on.
        var ratio = pairedRatio?.Value ?? candidateResult.MeanNs / referenceResult.MeanNs;

        var differenceIsReal = pairedRatio is { } estimate
            ? estimate.Lower > 1.0
            : !double.IsNaN(mwu.PValue) && mwu.PValue < significanceLevel;

        var practicallySignificant = ratio > maxSlowdownRatio;
        var isRegression = differenceIsReal && practicallySignificant;

        var referenceName = string.IsNullOrEmpty(referenceResult.Name) ? "reference" : referenceResult.Name;

        if (isRegression)
        {
            violations.Add(pairedRatio is { } paired
                ? $"Regression detected: {paired.Value:F2}x reference '{referenceName}' "
                  + $"[{paired.Lower:F2}-{paired.Upper:F2}x] over {paired.Replicates} paired replicates "
                  + $"({paired.ConfidenceLevel:P0} interval). MedianNs {candidateResult.MedianNs:F2} ns vs "
                  + $"{referenceResult.MedianNs:F2} ns. Exceeds the {maxSlowdownRatio:F2}x ratio gate by more "
                  + "than run-to-run variation."
                : $"Regression detected: mean {candidateResult.MeanNs:F2} ns vs reference '{referenceName}' {referenceResult.MeanNs:F2} ns " +
                  $"(ratio {ratio:F2}x, p={mwu.PValue:F4}, Cliff's delta={mwu.CliffsDelta:F3}). " +
                  $"Significant slowdown exceeding {maxSlowdownRatio:F2}x ratio gate.");
        }

        return new RelativeComparisonVerdict(violations, ratio, mwu.PValue, mwu.CliffsDelta, isRegression, pairedRatio);
    }

    private static ReadOnlySpan<double> AsSpan(IReadOnlyList<double> samples) =>
        samples as double[] ?? [.. samples];
}

/// <summary>
///     A structured pairwise regression verdict: the candidate/reference ratio, the
///     Mann-Whitney p-value, Cliff's delta, whether the comparison flagged a regression,
///     and the human-readable violation strings (empty when the comparison passed).
/// </summary>
/// <param name="Violations">Human-readable violation strings; empty when the comparison passed.</param>
/// <param name="Ratio">
///     The ratio the gate applied its threshold to: the paired per-replicate estimate when
///     <paramref name="Estimate" /> is present, otherwise candidate mean divided by reference mean.
///     <c>NaN</c> when undefined.
/// </param>
/// <param name="PValue">
///     The Mann-Whitney U p-value on the pooled samples; <c>NaN</c> when the test could not run.
///     Reported for context, and <b>not</b> what the verdict turns on when
///     <paramref name="Estimate" /> is present.
/// </param>
/// <param name="CliffsDelta">Cliff's delta effect size; <c>NaN</c> when the test could not run.</param>
/// <param name="IsRegression"><c>true</c> when the slowdown is both real and larger than the ratio gate.</param>
/// <param name="Estimate">
///     The paired ratio with its confidence interval, when the candidate and its reference were measured
///     co-resident across two or more replicates. <c>null</c> for a single-replicate comparison, where
///     the ratio is a point estimate and nothing in it says whether a re-run would agree.
/// </param>
internal sealed record RelativeComparisonVerdict(
    IReadOnlyList<string> Violations,
    double Ratio,
    double PValue,
    double CliffsDelta,
    bool IsRegression,
    RatioEstimate? Estimate = null);
