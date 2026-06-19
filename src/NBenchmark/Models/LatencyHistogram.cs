namespace NBenchmark;

public sealed record LatencyHistogram(
    IReadOnlyList<HistogramBucket> Buckets,
    double Min,
    double Max,
    int SampleCount);

public readonly record struct HistogramBucket(double Lower, double Upper, int Count);
