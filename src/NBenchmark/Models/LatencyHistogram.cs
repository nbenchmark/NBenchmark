namespace NBenchmark;

public sealed record LatencyHistogram(
    IReadOnlyList<HistogramBucket> Buckets,
    double MinNs,
    double MaxNs,
    int SampleCount);

public readonly record struct HistogramBucket(double Lower, double Upper, int Count);
