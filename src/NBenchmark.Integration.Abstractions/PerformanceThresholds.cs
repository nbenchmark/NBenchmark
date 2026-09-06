namespace NBenchmark.Integration.Abstractions;

public sealed class PerformanceThresholds
{
    public double? MaxMeanNs { get; init; }
    public double? MaxP95Ns { get; init; }
    public long? MaxAllocatedBytes { get; init; }
    public double MaxSlowdownRatio { get; init; } = 0;
    public int Samples { get; init; } = 0;
    public int WarmupSamples { get; init; } = 0;
    public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;
}
