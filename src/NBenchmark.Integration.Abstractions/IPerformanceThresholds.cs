namespace NBenchmark.Integration.Abstractions;

public interface IPerformanceThresholds
{
    public double MaxMeanNs { get; }
    public double MaxP95Ns { get; }
    public long MaxAllocatedBytes { get; }
    public string? BaselinePath { get; }
    public double MaxSlowdownRatio { get; }
    public int Iterations { get; }
    public int WarmupIterations { get; }
    public bool MeasureAllocations { get; }
    public OutlierMode OutlierMode { get; }
    public double ConfidenceLevel { get; }
    public double MaxAbsoluteThresholdTolerance { get; }
}
