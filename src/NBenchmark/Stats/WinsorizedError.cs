namespace NBenchmark.Stats;

/// <summary>
///     The Winsorized (Yuen) standard error of a trimmed mean: the uncertainty estimate that
///     accounts for the samples outlier trimming removed, rather than pretending they were never
///     collected.
/// </summary>
/// <remarks>
///     <para>
///         A plain t-interval on the trimmed set answers a question nobody asked: "how precisely
///         would this mean be known if the run had produced exactly these <c>h</c> samples and no
///         others?" The variance the fence removed simply disappears, so the reported margin
///         tightens by however much was trimmed - always in the same direction, and by two orders of
///         magnitude on a body whose variance lives entirely in its tail.
///     </para>
///     <para>
///         Winsorizing keeps every observation and clamps the trimmed ones to the nearest retained
///         value. An extreme sample therefore counts as an observation - it says the run produced
///         <c>n</c> readings, one of which was out past the fence - without its magnitude inflating
///         the interval, which is the property that makes the estimator robust rather than merely
///         wider. The scale is then corrected back to the trimmed mean's own sampling distribution
///         by <c>sqrt(n) / h</c>, and the interval is read on <c>h - 1</c> degrees of freedom.
///     </para>
///     <para>
///         <b>It reduces exactly.</b> With nothing trimmed, <c>h = n</c>, the Winsorized sample is
///         the original sample, and the result collapses to <c>s / sqrt(n)</c> on <c>n - 1</c>
///         degrees of freedom - the plain t-interval, unchanged. A clean run's numbers do not move.
///     </para>
///     <para>
///         <b>What it does not do.</b> Yuen makes the interval correct for what it describes - the
///         trimmed mean. It does not restore the uncertainty of the <i>raw</i> mean, because
///         clamping deliberately discards the outliers' magnitude. A body whose raw distribution is
///         far wider than its trimmed one is described by the tail percentiles (computed on the raw
///         set by default) and by the achieved CI width on the diagnostic, not by this interval.
///     </para>
/// </remarks>
public static class WinsorizedError
{
    /// <summary>
    ///     Computes the Winsorized standard error, margin of error and Winsorized standard
    ///     deviation for the trimming described by <paramref name="context" />, or <c>null</c> when
    ///     the estimator is undefined (fewer than two pre-trim samples, or fewer than two retained).
    /// </summary>
    /// <param name="context">The pre-trim sorted set and how many samples were trimmed from each end.</param>
    /// <param name="confidenceLevel">The two-tailed confidence level for the margin of error.</param>
    public static WinsorizedSpread? Compute(in TrimContext context, double confidenceLevel)
    {
        var sortedAll = context.SortedAll;

        if (sortedAll is null)
            return null;

        var n = sortedAll.Length;
        var trimmedLow = context.TrimmedLow;
        var trimmedHigh = context.TrimmedHigh;

        if (n < 2 || trimmedLow < 0 || trimmedHigh < 0)
            return null;

        var h = n - trimmedLow - trimmedHigh;

        if (h < 2)
            return null;

        // The clamp bounds are the innermost retained order statistics: x_(g_L+1) and x_(n-g_U),
        // one-based - the values the trimmed tails are pulled in to.
        var lowClamp = sortedAll[trimmedLow];
        var highClamp = sortedAll[n - trimmedHigh - 1];

        var sum = 0.0;

        for (var i = 0; i < n; i++)
        {
            sum += Winsorize(sortedAll, i, trimmedLow, trimmedHigh, lowClamp, highClamp);
        }

        var winsorizedMean = sum / n;
        var sumSq = 0.0;

        for (var i = 0; i < n; i++)
        {
            var d = Winsorize(sortedAll, i, trimmedLow, trimmedHigh, lowClamp, highClamp) - winsorizedMean;
            sumSq += d * d;
        }

        // The n-1 denominator, on the full Winsorized sample: the Winsorized standard deviation is a
        // property of all n readings, and the sqrt(n)/h factor below is what rescales it onto the
        // trimmed mean's sampling distribution. Dividing by h-1 here as well would double-count.
        var winsorizedStdDev = Math.Sqrt(sumSq / (n - 1));
        var standardError = winsorizedStdDev * Math.Sqrt(n) / h;

        var degreesOfFreedom = h - 1;
        var tCritical = StudentT.CriticalValue(confidenceLevel, degreesOfFreedom);
        var marginOfError = double.IsNaN(tCritical) ? 0.0 : tCritical * standardError;

        return new WinsorizedSpread(winsorizedStdDev, standardError, marginOfError, degreesOfFreedom);
    }

    private static double Winsorize(
        double[] sortedAll, int index, int trimmedLow, int trimmedHigh, double lowClamp, double highClamp)
    {
        if (index < trimmedLow)
            return lowClamp;

        return index >= sortedAll.Length - trimmedHigh ? highClamp : sortedAll[index];
    }
}

/// <summary>
///     How many samples outlier trimming removed from each end of a sorted sample set, alongside the
///     pre-trim set itself - the inputs the Winsorized standard error needs and the trimmed array
///     alone cannot supply.
/// </summary>
/// <param name="SortedAll">The full pre-trim sample set, sorted ascending. Never mutated.</param>
/// <param name="TrimmedLow">Samples discarded from the fast end (<c>g_L</c>).</param>
/// <param name="TrimmedHigh">Samples discarded from the slow end (<c>g_U</c>).</param>
public readonly record struct TrimContext(double[] SortedAll, int TrimmedLow, int TrimmedHigh)
{
    /// <summary>Whether any sample was trimmed at all. When false the Winsorized estimator reduces to the plain one.</summary>
    public bool IsTrimmed => TrimmedLow > 0 || TrimmedHigh > 0;

    /// <summary>
    ///     Derives the context from a completed trim.
    ///     <para>
    ///         The counts are read from where the discarded values sit relative to the retained
    ///         range rather than from <see cref="TrimResult.Discarded" />'s length, because a
    ///         detector is free to discard from one end, both, or - a custom detector - from the
    ///         middle. A discard that lies inside the retained range is neither a low nor a high
    ///         trim: it stays in the Winsorized sample unclamped and counts toward <c>h</c>, which
    ///         is the only reading under which the estimator's <c>sqrt(n) / h</c> scale still
    ///         describes the same <c>n</c> observations.
    ///     </para>
    /// </summary>
    public static TrimContext From(in TrimResult trim)
    {
        var kept = trim.Kept;
        var discarded = trim.Discarded;

        if (kept.Length == 0 || discarded.Length == 0)
            return new TrimContext(trim.SortedAll, 0, 0);

        var lowest = kept[0];
        var highest = kept[^1];
        var low = 0;
        var high = 0;

        foreach (var value in discarded)
        {
            if (value < lowest)
                low++;
            else if (value > highest)
                high++;
        }

        return new TrimContext(trim.SortedAll, low, high);
    }
}

/// <summary>
///     The output of <see cref="WinsorizedError.Compute" />: the Winsorized standard deviation, the
///     standard error of the trimmed mean derived from it, the corresponding margin of error, and
///     the degrees of freedom the interval was read on.
/// </summary>
/// <param name="StandardDeviation">The Winsorized standard deviation <c>s_w</c> (with the <c>n - 1</c> denominator).</param>
/// <param name="StandardError"><c>s_w × sqrt(n) / h</c> - the standard error of the trimmed mean.</param>
/// <param name="MarginOfError"><c>t* × StandardError</c> at the requested confidence level.</param>
/// <param name="DegreesOfFreedom"><c>h - 1</c>, where <c>h</c> is the number of retained samples.</param>
public readonly record struct WinsorizedSpread(
    double StandardDeviation,
    double StandardError,
    double MarginOfError,
    int DegreesOfFreedom);
