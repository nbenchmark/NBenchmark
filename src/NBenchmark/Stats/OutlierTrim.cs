namespace NBenchmark.Stats;

/// <summary>
///     Outlier-trimming strategies applied to a raw timings array before stats
///     computation. The returned array is always sorted in ascending order, so
///     callers can hand it to <see cref="Percentile.Compute" /> directly. Moved
///     from <c>BenchmarkRunner</c> so the trim logic is directly testable in
///     isolation - in particular, the all-filtered-fallback branch in
///     <see cref="OutlierMode.IqrFence" />.
/// </summary>
internal static class OutlierTrim
{
    public static double[] Trim(double[] timings, OutlierMode mode) => TrimDetailed(timings, mode).Kept;

    /// <summary>
    ///     Trims by <paramref name="mode" /> and reports both the kept samples (sorted
    ///     ascending) and the discarded ones (sorted ascending). The discarded set feeds
    ///     bimodal-cluster diagnostics; callers that only need the kept array use
    ///     <see cref="Trim" />.
    /// </summary>
    public static TrimResult TrimDetailed(double[] timings, OutlierMode mode) => mode switch
    {
        OutlierMode.None => new TrimResult(SortAndReturn(timings), []),
        OutlierMode.RemoveTop5Percent => RemoveTopPercent(timings, 0.05),
        OutlierMode.RemoveTopAndBottom5Percent => RemoveBothPercent(timings, 0.05),
        OutlierMode.IqrFence => RemoveIqrOutliers(timings),
        _ => new TrimResult(timings, []),
    };

    private static double[] SortAndReturn(double[] values)
    {
        Array.Sort(values);
        return values;
    }

    private static TrimResult RemoveTopPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var keep = (int)Math.Floor(values.Length * (1.0 - fraction));
        return new TrimResult(values[..keep], values[keep..]);
    }

    private static TrimResult RemoveBothPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var trimEach = (int)Math.Floor(values.Length * fraction);
        var kept = values[trimEach..(values.Length - trimEach)];
        var discarded = new double[trimEach * 2];
        Array.Copy(values, 0, discarded, 0, trimEach);
        Array.Copy(values, values.Length - trimEach, discarded, trimEach, trimEach);
        Array.Sort(discarded);
        return new TrimResult(kept, discarded);
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
            return new TrimResult(values, []);

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

        return new TrimResult(result, discarded);
    }
}

/// <summary>
///     The outcome of an outlier trim: <see cref="Kept" /> samples retained for stats
///     (sorted ascending) and <see cref="Discarded" /> samples removed (sorted ascending).
/// </summary>
internal readonly record struct TrimResult(double[] Kept, double[] Discarded);
