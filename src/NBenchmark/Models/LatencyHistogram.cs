namespace NBenchmark;

/// <summary>
///     A latency histogram over a benchmark's samples: equal-width buckets spanning
///     <see cref="MinNs" /> to <see cref="MaxNs" />. Follows the same raw-vs-trimmed basis as
///     <see cref="BenchmarkResult.Histogram" /> - see <see cref="TailMetricsBasis" />.
/// </summary>
/// <param name="Buckets">The histogram's buckets, in ascending order.</param>
/// <param name="MinNs">The minimum sample value (nanoseconds) the histogram spans.</param>
/// <param name="MaxNs">The maximum sample value (nanoseconds) the histogram spans.</param>
/// <param name="SampleCount">The number of samples the histogram was built from.</param>
public sealed record LatencyHistogram(
    IReadOnlyList<HistogramBucket> Buckets,
    double MinNs,
    double MaxNs,
    int SampleCount);

/// <summary>
///     One bucket of a <see cref="LatencyHistogram" />: the half-open range <c>[Lower, Upper)</c>
///     (the final bucket's <see cref="Upper" /> is inclusive, since it equals the sample maximum)
///     and how many samples fell in it.
/// </summary>
/// <param name="Lower">The bucket's lower bound (nanoseconds), inclusive.</param>
/// <param name="Upper">The bucket's upper bound (nanoseconds).</param>
/// <param name="Count">The number of samples in the bucket.</param>
public readonly record struct HistogramBucket(double Lower, double Upper, int Count);
