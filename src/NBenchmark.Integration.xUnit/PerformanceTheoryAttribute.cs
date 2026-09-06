using NBenchmark.Integration.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

[XunitTestCaseDiscoverer(
    "NBenchmark.Integration.xUnit.PerformanceTheoryDiscoverer",
    "NBenchmark.Integration.xUnit")]
public sealed class PerformanceTheoryAttribute : TheoryAttribute, IPerformanceThresholds
{
    public double MaxMeanNs { get; init; } = -1;
    public double MaxP95Ns { get; init; } = -1;
    public long MaxAllocatedBytes { get; init; } = -1;
    public string? ReferenceMethod { get; init; }
    public double MaxSlowdownRatio { get; init; } = 0;
    public int Samples { get; init; }
    public int WarmupSamples { get; init; }
    public bool MeasureAllocations { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;
    public double ConfidenceLevel { get; init; } = 0.95;
    public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;

    /// <summary>
    ///     Worker processes to measure each test case in. Defaults to 1; two or more give the ratio gate
    ///     a paired confidence interval. See <see cref="IPerformanceThresholds.LaunchCount" />.
    ///     <para>
    ///         Spent per test case, so a theory with eight cases and <c>LaunchCount = 3</c> launches
    ///         twenty-four workers.
    ///     </para>
    /// </summary>
    public int LaunchCount { get; init; } = 1;

    // No RequireIsolation property - see PerformanceFactAttribute. It defaults to true via
    // IPerformanceThresholds and the opt-out is [AllowInProcessGate].
}
