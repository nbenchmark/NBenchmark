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
    public static double[] Trim(double[] timings, OutlierMode mode) => mode switch
    {
        OutlierMode.None => SortAndReturn(timings),
        OutlierMode.RemoveTop5Percent => RemoveTopPercent(timings, 0.05),
        OutlierMode.RemoveTop5PercentAndBottom5Percent => RemoveBothPercent(timings, 0.05),
        OutlierMode.IqrFence => RemoveIqrOutliers(timings),
        _ => timings,
    };

    private static double[] SortAndReturn(double[] values)
    {
        Array.Sort(values);
        return values;
    }

    private static double[] RemoveTopPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var keep = (int)Math.Floor(values.Length * (1.0 - fraction));
        return values[..keep];
    }

    private static double[] RemoveBothPercent(double[] values, double fraction)
    {
        Array.Sort(values);
        var trimEach = (int)Math.Floor(values.Length * fraction);
        return values[trimEach..(values.Length - trimEach)];
    }

    private static double[] RemoveIqrOutliers(double[] values)
    {
        Array.Sort(values);
        var q1 = Percentile.Compute(values, 0.25);
        var q3 = Percentile.Compute(values, 0.75);
        var iqr = q3 - q1;
        var lower = q1 - 1.5 * iqr;
        var upper = q3 + 1.5 * iqr;
        var filtered = values.Where(v => v >= lower && v <= upper).ToArray();

        return filtered.Length > 0 ? filtered : values;
    }
}
