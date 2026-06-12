namespace NBenchmark.Stats;

internal static class OutlierTrim
{
    public static double[] Trim(double[] timings, OutlierMode mode) => TrimDetailed(timings, mode).Kept;

    public static TrimResult TrimDetailed(double[] timings, OutlierMode mode) => mode switch
    {
        OutlierMode.None => BuildNoneResult(timings),
        OutlierMode.RemoveTop5Percent => RemoveTopPercent(timings, 0.05),
        OutlierMode.RemoveTopAndBottom5Percent => RemoveBothPercent(timings, 0.05),
        OutlierMode.IqrFence => RemoveIqrOutliers(timings),
        _ => BuildNoneResult(timings),
    };

    private static TrimResult BuildNoneResult(double[] values)
    {
        Array.Sort(values);
        var q1 = Percentile.Compute(values, 0.25);
        var q3 = Percentile.Compute(values, 0.75);
        var iqr = q3 - q1;
        return new TrimResult(values, [], q1, q3, iqr, null, null);
    }

    private static TrimResult RemoveTopPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var q1 = Percentile.Compute(values, 0.25);
        var q3 = Percentile.Compute(values, 0.75);
        var iqr = q3 - q1;
        var keep = (int)Math.Floor(values.Length * (1.0 - fraction));
        return new TrimResult(values[..keep], values[keep..], q1, q3, iqr, null, null);
    }

    private static TrimResult RemoveBothPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var q1 = Percentile.Compute(values, 0.25);
        var q3 = Percentile.Compute(values, 0.75);
        var iqr = q3 - q1;
        var trimEach = (int)Math.Floor(values.Length * fraction);
        var kept = values[trimEach..(values.Length - trimEach)];
        var discarded = new double[trimEach * 2];
        Array.Copy(values, 0, discarded, 0, trimEach);
        Array.Copy(values, values.Length - trimEach, discarded, trimEach, trimEach);
        Array.Sort(discarded);
        return new TrimResult(kept, discarded, q1, q3, iqr, null, null);
    }

    private static TrimResult RemoveIqrOutliers(double[] values)
    {
        Array.Sort(values);
        var q1 = Percentile.Compute(values, 0.25);
        var q3 = Percentile.Compute(values, 0.75);
        var iqr = q3 - q1;
        var lower = q1 - 1.5 * iqr;
        var upper = q3 + 1.5 * iqr;

        var keep = 0;

        foreach (var t in values)
        {
            if (t >= lower && t <= upper)
                keep++;
        }

        if (keep == 0 || keep == values.Length)
            return new TrimResult(values, [], q1, q3, iqr, lower, upper);

        var result = new double[keep];
        var discarded = new double[values.Length - keep];
        var write = 0;
        var writeDiscarded = 0;

        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];

            if (v >= lower && v <= upper)
                result[write++] = v;
            else
                discarded[writeDiscarded++] = v;
        }

        return new TrimResult(result, discarded, q1, q3, iqr, lower, upper);
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
