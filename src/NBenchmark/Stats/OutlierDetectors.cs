namespace NBenchmark.Stats;

/// <summary>
///     Built-in <see cref="IOutlierDetector" /> implementations and the mapping from the
///     convenience <see cref="OutlierMode" /> enum onto detector instances.
/// </summary>
public static class OutlierDetectors
{
    /// <summary>Keeps every sample (<see cref="OutlierMode.None" />).</summary>
    public static IOutlierDetector None { get; } = new NoOutlierDetector();

    /// <summary>Trims the slowest 5% (<see cref="OutlierMode.RemoveTop5Percent" />).</summary>
    public static IOutlierDetector RemoveTop5Percent { get; } = new TopPercentileOutlierDetector();

    /// <summary>Trims the fastest and slowest 5% (<see cref="OutlierMode.RemoveTopAndBottom5Percent" />).</summary>
    public static IOutlierDetector RemoveTopAndBottom5Percent { get; } = new TwoSidedPercentileOutlierDetector();

    /// <summary>Tukey's 1.5×IQR fence (<see cref="OutlierMode.IqrFence" />).</summary>
    public static IOutlierDetector IqrFence { get; } = new IqrFenceOutlierDetector();

    /// <summary>Median Absolute Deviation, 3× scaled-MAD fence (<see cref="OutlierMode.MedianAbsoluteDeviation" />).</summary>
    public static IOutlierDetector MedianAbsoluteDeviation { get; } = new MadOutlierDetector();

    /// <summary>Resolves the built-in detector that corresponds to <paramref name="mode" />.</summary>
    public static IOutlierDetector ForMode(OutlierMode mode) => mode switch
    {
        OutlierMode.None => None,
        OutlierMode.RemoveTop5Percent => RemoveTop5Percent,
        OutlierMode.RemoveTopAndBottom5Percent => RemoveTopAndBottom5Percent,
        OutlierMode.IqrFence => IqrFence,
        OutlierMode.MedianAbsoluteDeviation => MedianAbsoluteDeviation,
        _ => IqrFence,
    };

    /// <summary>
    ///     Partitions a pre-sorted array into kept/discarded using a predicate over each value.
    ///     Order is preserved, so both output arrays remain sorted ascending.
    /// </summary>
    internal static OutlierClassification Partition(
        double[] sorted, Func<double, bool> keep, double? lowerFence, double? upperFence)
    {
        var keptCount = 0;

        foreach (var v in sorted)
        {
            if (keep(v))
                keptCount++;
        }

        // Never discard everything - the engine always needs samples to summarize.
        if (keptCount == 0 || keptCount == sorted.Length)
            return new OutlierClassification { Kept = sorted, Discarded = [], LowerFence = lowerFence, UpperFence = upperFence };

        var kept = new double[keptCount];
        var discarded = new double[sorted.Length - keptCount];
        var w = 0;
        var d = 0;

        foreach (var v in sorted)
        {
            if (keep(v))
                kept[w++] = v;
            else
                discarded[d++] = v;
        }

        return new OutlierClassification { Kept = kept, Discarded = discarded, LowerFence = lowerFence, UpperFence = upperFence };
    }
}

/// <summary>Keeps every sample. No trimming.</summary>
public sealed class NoOutlierDetector : IOutlierDetector
{
    public string Name => "none";

    public OutlierClassification Classify(double[] sortedSamples) =>
        OutlierClassification.KeepAll(sortedSamples);
}

/// <summary>
///     Discards the slowest <c>ceil(n × fraction)</c> samples (equivalently keeps the
///     fastest <c>floor(n × (1 − fraction))</c>). A one-sided trim that protects the median
///     from rare slow spikes while keeping the fast tail intact.
/// </summary>
public sealed class TopPercentileOutlierDetector(double fraction = 0.05) : IOutlierDetector
{
    private readonly double _fraction = fraction is > 0 and < 1
        ? fraction
        : throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "fraction must be strictly between 0 and 1.");

    public string Name => $"top {_fraction * 100:0.#}%";

    public OutlierClassification Classify(double[] sortedSamples)
    {
        var keep = (int)Math.Floor(sortedSamples.Length * (1.0 - _fraction));

        if (keep <= 0 || keep >= sortedSamples.Length)
            return OutlierClassification.KeepAll(sortedSamples);

        return new OutlierClassification
        {
            Kept = sortedSamples[..keep],
            Discarded = sortedSamples[keep..],
        };
    }
}

/// <summary>
///     Discards the fastest and slowest <c>floor(n × fraction)</c> samples from each tail.
///     A symmetric trimmed sample, robust to outliers on both ends.
/// </summary>
public sealed class TwoSidedPercentileOutlierDetector(double fraction = 0.05) : IOutlierDetector
{
    private readonly double _fraction = fraction is > 0 and < 0.5
        ? fraction
        : throw new ArgumentOutOfRangeException(nameof(fraction), fraction, "fraction must be strictly between 0 and 0.5.");

    public string Name => $"top & bottom {_fraction * 100:0.#}%";

    public OutlierClassification Classify(double[] sortedSamples)
    {
        var trimEach = (int)Math.Floor(sortedSamples.Length * _fraction);

        if (trimEach <= 0)
            return OutlierClassification.KeepAll(sortedSamples);

        var kept = sortedSamples[trimEach..(sortedSamples.Length - trimEach)];

        if (kept.Length == 0)
            return OutlierClassification.KeepAll(sortedSamples);

        var discarded = new double[trimEach * 2];
        Array.Copy(sortedSamples, 0, discarded, 0, trimEach);
        Array.Copy(sortedSamples, sortedSamples.Length - trimEach, discarded, trimEach, trimEach);

        return new OutlierClassification { Kept = kept, Discarded = discarded };
    }
}

/// <summary>
///     Tukey's fence: discards any sample below <c>Q1 − k × IQR</c> or above
///     <c>Q3 + k × IQR</c> (<c>k = 1.5</c> by default). The library default.
/// </summary>
public sealed class IqrFenceOutlierDetector(double k = 1.5) : IOutlierDetector
{
    private readonly double _k = k > 0
        ? k
        : throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive.");

    public string Name => $"IQR fence ({_k:0.#}×)";

    public OutlierClassification Classify(double[] sortedSamples)
    {
        if (sortedSamples.Length == 0)
            return OutlierClassification.KeepAll(sortedSamples);

        var q1 = Percentile.Compute(sortedSamples, 0.25);
        var q3 = Percentile.Compute(sortedSamples, 0.75);
        var iqr = q3 - q1;
        var lower = q1 - _k * iqr;
        var upper = q3 + _k * iqr;

        return OutlierDetectors.Partition(sortedSamples, v => v >= lower && v <= upper, lower, upper);
    }
}

/// <summary>
///     Median Absolute Deviation fence. Computes the median <c>m</c> and the scaled MAD
///     <c>1.4826 × median(|xᵢ − m|)</c>, then discards any sample whose distance from the
///     median exceeds <c>threshold × scaledMAD</c> (<c>threshold = 3.0</c> by default,
///     i.e. an Iglewicz–Hoaglin modified z-score cut-off).
///     <para>
///         MAD is far more resilient than the IQR fence to heavy-tailed, skewed
///         distributions because the scale estimate itself has a ~50% breakdown point: up
///         to half the samples can be extreme before the cut-off is distorted. The
///         <c>1.4826</c> factor makes the scaled MAD a consistent estimator of the standard
///         deviation for normally distributed data, so the threshold reads like a number of
///         standard deviations.
///     </para>
/// </summary>
public sealed class MadOutlierDetector(double threshold = 3.0) : IOutlierDetector
{
    private const double NormalConsistencyFactor = 1.4826;

    private readonly double _threshold = threshold > 0
        ? threshold
        : throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "threshold must be positive.");

    public string Name => $"MAD ({_threshold:0.#}×)";

    public OutlierClassification Classify(double[] sortedSamples)
    {
        if (sortedSamples.Length < 3)
            return OutlierClassification.KeepAll(sortedSamples);

        var median = Percentile.Compute(sortedSamples, 0.50);

        var absDeviations = new double[sortedSamples.Length];

        for (var i = 0; i < sortedSamples.Length; i++)
        {
            absDeviations[i] = Math.Abs(sortedSamples[i] - median);
        }

        Array.Sort(absDeviations);
        var scaledMad = Percentile.Compute(absDeviations, 0.50) * NormalConsistencyFactor;

        // A zero scale means more than half the samples are identical; there is no robust
        // spread to fence against, so keep everything (mirrors the IQR fence fallback).
        if (scaledMad == 0)
            return OutlierClassification.KeepAll(sortedSamples);

        var bound = _threshold * scaledMad;
        var lower = median - bound;
        var upper = median + bound;

        return OutlierDetectors.Partition(sortedSamples, v => v >= lower && v <= upper, lower, upper);
    }
}
