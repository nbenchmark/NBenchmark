using NBenchmark.Integration.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

/// <summary>
///     Marks an xUnit theory as a performance benchmark, gated on the thresholds set here and applied to
///     every data row. Discovered via <see cref="PerformanceTheoryDiscoverer" />, which turns each row into
///     a <see cref="PerformanceTestCase" />.
/// </summary>
[XunitTestCaseDiscoverer(
    "NBenchmark.Integration.xUnit.PerformanceTheoryDiscoverer",
    "NBenchmark.Integration.xUnit")]
public sealed class PerformanceTheoryAttribute : TheoryAttribute, IPerformanceThresholds
{
    /// <summary>Maximum mean time per operation in nanoseconds, or <see cref="IPerformanceThresholds.Unset" />.</summary>
    public double MaxMeanNs { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>
    ///     Maximum median time per operation in nanoseconds. The median is the statistic the reports
    ///     lead with; prefer it over <see cref="MaxMeanNs" /> unless the average is what is meant.
    ///     See <see cref="IPerformanceThresholds.MaxMedianNs" />.
    /// </summary>
    public double MaxMedianNs { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>Maximum 95th-percentile time per operation in nanoseconds, or <see cref="IPerformanceThresholds.Unset" />.</summary>
    public double MaxP95Ns { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>Maximum mean bytes allocated per operation, or <see cref="IPerformanceThresholds.UnsetBytes" />.</summary>
    public long MaxAllocatedBytes { get; init; } = IPerformanceThresholds.UnsetBytes;

    /// <summary>
    ///     The name of the method to measure alongside this one as the denominator of
    ///     <see cref="MaxSlowdownRatio" />, or <c>null</c> when there is no comparison.
    /// </summary>
    public string? ReferenceMethod { get; init; }

    /// <summary>
    ///     Maximum ratio of this measurement to <see cref="ReferenceMethod" />'s, or
    ///     <see cref="IPerformanceThresholds.Unset" />. Reads as "no more than N times slower than the reference".
    /// </summary>
    public double MaxSlowdownRatio { get; init; } = IPerformanceThresholds.Unset;

    /// <summary>Measured samples to take, or <see cref="IPerformanceThresholds.AutoSampleCount" />.</summary>
    public int Samples { get; init; } = IPerformanceThresholds.AutoSampleCount;

    /// <summary>Warmup samples to take before measuring, or <see cref="IPerformanceThresholds.AutoSampleCount" />.</summary>
    public int WarmupSamples { get; init; } = IPerformanceThresholds.AutoSampleCount;

    /// <summary>Whether to measure allocations as well as time.</summary>
    public bool MeasureAllocations { get; init; }

    /// <summary>Which samples to trim before the statistics are computed.</summary>
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;

    /// <summary>The confidence level for the reported interval, e.g. <c>0.95</c>.</summary>
    public double ConfidenceLevel { get; init; } = 0.95;

    /// <summary>
    ///     How far past an absolute threshold a measurement may land before the gate fails, as a
    ///     multiplier of the threshold. <c>1.0</c> fails at the threshold exactly.
    /// </summary>
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
