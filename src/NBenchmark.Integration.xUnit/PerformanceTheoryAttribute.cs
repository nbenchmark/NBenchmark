using NBenchmark.Integration.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

[XunitTestCaseDiscoverer(
    "NBenchmark.Integration.xUnit.PerformanceTheoryDiscoverer",
    "NBenchmark.Integration.xUnit")]
public sealed class PerformanceTheoryAttribute : TheoryAttribute, IPerformanceThresholds
{
    public double MaxMeanNs { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>
    ///     Maximum median time per operation in nanoseconds. The median is the statistic the reports
    ///     lead with; prefer it over <see cref="MaxMeanNs" /> unless the average is what is meant.
    ///     See <see cref="IPerformanceThresholds.MaxMedianNs" />.
    /// </summary>
    public double MaxMedianNs { get; init; } = IPerformanceThresholds.Unset;
    public double MaxP95Ns { get; init; } = IPerformanceThresholds.Unset;
    public long MaxAllocatedBytes { get; init; } = IPerformanceThresholds.UnsetBytes;
    public string? ReferenceMethod { get; init; }
    public double MaxSlowdownRatio { get; init; } = IPerformanceThresholds.Unset;
    public int Samples { get; init; } = IPerformanceThresholds.AutoSampleCount;
    public int WarmupSamples { get; init; } = IPerformanceThresholds.AutoSampleCount;
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
