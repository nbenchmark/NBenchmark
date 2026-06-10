namespace NBenchmark.Extensions.Abstractions;

internal sealed class PerformanceThresholds
{
    public double? MaxMeanNs { get; init; }
    public double? MaxP95Ns { get; init; }
    public long? MaxAllocatedBytes { get; init; }
    public string? BaselinePath { get; init; }
    public double MaxSlowdownRatio { get; init; } = 1.2;
    public int Iterations { get; init; } = 0;
    public int WarmupIterations { get; init; } = 0;
}
