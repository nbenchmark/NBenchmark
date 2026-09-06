namespace NBenchmark.Stats;

/// <summary>
///     Post-hoc i.i.d. sanity checks on the measured stream. Both the CI-width stop rule and the
///     Mann-Whitney test assume independent, identically distributed samples; drift (a JIT tier-up
///     or DPGO step landing mid-measurement, a thermal ramp, periodic GC) and autocorrelation both
///     shrink the computed interval faster than the truth warrants, so an honest-looking ±2.5%
///     can undercover. These two cheap checks turn that silent failure into a visible warning.
/// </summary>
internal static class SampleQuality
{
    /// <summary>Below this raw sample count the checks are skipped - too little power to be meaningful.</summary>
    internal const int MinSamplesForChecks = 50;

    /// <summary>Split-half Mann-Whitney p-value below which a drift warning fires.</summary>
    internal const double DriftPValueThreshold = 0.001;

    /// <summary>
    ///     The relative gap between the two half-medians a drift must also exceed before the warning
    ///     fires, as a fraction of the smaller half-median. Mirrors the engine's in-loop drift gate so
    ///     the two cannot contradict each other.
    ///     <para>
    ///         Significance alone is not enough at the sample counts the measurement time floor now
    ///         produces. A rank test on 2,000 samples resolves a sub-percent shift to p far below the
    ///         threshold, so a bare p-value rule would warn "the halves differ significantly" on
    ///         precisely the runs the engine's gate just certified as steady - contradictory advice on
    ///         a difference too small to act on. Requiring practical magnitude as well keeps the
    ///         warning about drift that matters.
    ///     </para>
    /// </summary>
    internal const double DriftRelativeThreshold = 0.10;

    /// <summary>Lag-1 autocorrelation above which a dependence warning fires.</summary>
    internal const double AutocorrelationThreshold = 0.5;

    /// <summary>
    ///     Runs the drift and autocorrelation checks over the raw stream in arrival order and
    ///     returns any warnings. Empty when <paramref name="rawArrivalOrder" /> has fewer than
    ///     <see cref="MinSamplesForChecks" /> samples or both checks pass.
    /// </summary>
    /// <param name="rawArrivalOrder">The pre-trim measured samples in the order they were collected.</param>
    public static IReadOnlyList<string> BuildWarnings(double[] rawArrivalOrder)
    {
        ArgumentNullException.ThrowIfNull(rawArrivalOrder);

        var n = rawArrivalOrder.Length;

        if (n < MinSamplesForChecks)
            return [];

        var warnings = new List<string>(2);

        // Drift: compare the first and second halves of the arrival-order stream. A shift that is both
        // statistically significant and practically large means the distribution moved during
        // measurement. Both conditions are required - see DriftRelativeThreshold for why significance
        // alone would contradict the engine's own in-loop drift gate.
        var half = n / 2;
        var first = rawArrivalOrder[..half];
        var second = rawArrivalOrder[half..];
        var drift = MannWhitneyU.Test(first, second);

        if (!double.IsNaN(drift.PValue) && drift.PValue < DriftPValueThreshold)
        {
            var relativeShift = RelativeMedianShift(first, second);

            if (relativeShift > DriftRelativeThreshold)
            {
                warnings.Add(
                    $"the first and second halves of the measured stream differ significantly "
                    + $"(split-half Mann-Whitney p = {FormatP(drift.PValue)}, medians {relativeShift * 100:F1}% apart) - "
                    + "the timings drifted during measurement (JIT tier-up/DPGO, thermal ramp, or periodic GC), "
                    + "so the reported confidence interval describes a moving target and may understate the true "
                    + "uncertainty; consider a longer warmup (--min-warmup-time) or checking host thermal/load state.");
            }
        }

        // Dependence: lag-1 autocorrelation. Positive correlation between consecutive samples
        // deflates the effective sample size below the nominal n.
        var r1 = Lag1Autocorrelation(rawArrivalOrder);

        if (r1 > AutocorrelationThreshold)
        {
            var effectiveN = n * (1.0 - r1) / (1.0 + r1);

            warnings.Add(
                $"consecutive samples are correlated (lag-1 autocorrelation r = {r1:F2}) - the samples "
                + "are not independent, so the confidence interval understates uncertainty "
                + $"(effective sample size ≈ {effectiveN:F0} of {n}).");
        }

        return warnings;
    }

    /// <summary>
    ///     The lag-1 sample autocorrelation coefficient of <paramref name="values" /> in the given
    ///     order. Returns 0 when the series has no variance.
    /// </summary>
    internal static double Lag1Autocorrelation(double[] values)
    {
        var n = values.Length;

        if (n < 2)
            return 0.0;

        var mean = 0.0;

        for (var i = 0; i < n; i++)
        {
            mean += values[i];
        }

        mean /= n;

        var numerator = 0.0;
        var denominator = 0.0;

        for (var i = 0; i < n; i++)
        {
            var d = values[i] - mean;
            denominator += d * d;

            if (i > 0)
                numerator += d * (values[i - 1] - mean);
        }

        return denominator > 0 ? numerator / denominator : 0.0;
    }

    private static string FormatP(double p) => p < 1e-6 ? p.ToString("0.0e+0") : p.ToString("0.######");

    /// <summary>
    ///     The gap between the two halves' medians as a fraction of the smaller one, or <c>0</c> when
    ///     either half is empty or degenerate. Medians rather than means, because the heavy-tailed
    ///     bodies this check runs on have means dominated by their tails - a single GC pause in one half
    ///     would otherwise read as drift.
    /// </summary>
    private static double RelativeMedianShift(double[] first, double[] second)
    {
        if (first.Length == 0 || second.Length == 0)
            return 0.0;

        var a = Median(first);
        var b = Median(second);

        if (!double.IsFinite(a) || !double.IsFinite(b) || a <= 0 || b <= 0)
            return 0.0;

        return Math.Abs(b - a) / Math.Min(a, b);
    }

    private static double Median(double[] values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;

        return sorted.Length % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
}
