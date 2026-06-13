namespace NBenchmark.Stats;

/// <summary>
///     Bridges the engine to an <see cref="IOutlierDetector" />: it computes the quartile
///     descriptive statistics (Q1/Q3/IQR) that every report shows - independent of the
///     trimming strategy - and delegates the keep/discard decision to the detector.
/// </summary>
internal static class OutlierTrim
{
    public static double[] Trim(double[] timings, OutlierMode mode) => TrimDetailed(timings, mode).Kept;

    public static TrimResult TrimDetailed(double[] timings, OutlierMode mode) =>
        TrimDetailed(timings, OutlierDetectors.ForMode(mode));

    public static TrimResult TrimDetailed(double[] timings, IOutlierDetector detector)
    {
        Array.Sort(timings);

        var q1 = Percentile.Compute(timings, 0.25);
        var q3 = Percentile.Compute(timings, 0.75);
        var iqr = q3 - q1;

        var classification = detector.Classify(timings);

        return new TrimResult(
            classification.Kept,
            classification.Discarded,
            q1,
            q3,
            iqr,
            classification.LowerFence,
            classification.UpperFence);
    }
}

internal readonly record struct TrimResult(
    double[] Kept,
    double[] Discarded,
    double Q1,
    double Q3,
    double InterquartileRange,
    double? LowerFence,
    double? UpperFence);
