namespace NBenchmark.Extensions.MSTest;

public sealed class PerformanceAssertionOptions
{
    public double MaxMeanNs { get; init; } = -1;
    public double MaxP95Ns { get; init; } = -1;
    public long MaxAllocatedBytes { get; init; } = -1;
    public string? BaselinePath { get; init; }
    public double MaxSlowdownRatio { get; init; } = 1.2;
    public int Iterations { get; init; }
    public int WarmupIterations { get; init; }
    public bool MeasureAllocations { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.RemoveTop5Percent;
    public double ConfidenceLevel { get; init; } = 0.95;
}
