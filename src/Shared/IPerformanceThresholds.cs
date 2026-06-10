namespace NBenchmark.Extensions.Abstractions;

internal interface IPerformanceThresholds
{
    double MaxMeanNs { get; }
    double MaxP95Ns { get; }
    long MaxAllocatedBytes { get; }
    string? BaselinePath { get; }
    double MaxSlowdownRatio { get; }
    int Iterations { get; }
    int WarmupIterations { get; }
    bool MeasureAllocations { get; }
    OutlierMode OutlierMode { get; }
    double ConfidenceLevel { get; }
}