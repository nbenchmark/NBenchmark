using NBenchmark.Integration.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

[XunitTestCaseDiscoverer(
    "NBenchmark.Integration.xUnit.PerformanceFactDiscoverer",
    "NBenchmark.Integration.xUnit")]
public sealed class PerformanceFactAttribute : FactAttribute, IPerformanceThresholds
{
    public double MaxMeanNs { get; init; } = -1;
    public double MaxP95Ns { get; init; } = -1;
    public long MaxAllocatedBytes { get; init; } = -1;
    public string? ReferenceMethod { get; init; }
    public double MaxSlowdownRatio { get; init; } = 0;
    public int Iterations { get; init; }
    public int WarmupIterations { get; init; }
    public bool MeasureAllocations { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;
    public double ConfidenceLevel { get; init; } = 0.95;
    public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;

    /// <summary>
    ///     Fails the test when the measurement was taken in the test host rather than in a worker
    ///     process. See <see cref="IPerformanceThresholds.RequireIsolation" />.
    /// </summary>
    public bool RequireIsolation { get; init; }
}
