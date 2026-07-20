namespace NBenchmark;

/// <summary>
///     Which sample set the order statistics - percentiles, min, max, and the histogram - are
///     computed from. Central-tendency and dispersion statistics (mean, standard deviation,
///     CI, CV, skewness, kurtosis, MAD) always stay on the trimmed set regardless of this
///     setting; only the tail metrics are affected.
/// </summary>
public enum TailMetricsBasis
{
    /// <summary>
    ///     Compute tail metrics from the full pre-trim sample set (the default). The IQR/MAD fence
    ///     removes exactly the slow tail that P99/P99.9/Max exist to describe, so reporting those on
    ///     the raw set keeps them honest - a GC pause the <c>Realistic</c> profile deliberately
    ///     timed is visible in Max rather than trimmed back out of it.
    /// </summary>
    Raw,

    /// <summary>
    ///     Compute tail metrics from the trimmed (inlier) set, matching the pre-P2-1 behavior. Use
    ///     this to describe only the central process, excluding interference spikes.
    /// </summary>
    Trimmed,
}
