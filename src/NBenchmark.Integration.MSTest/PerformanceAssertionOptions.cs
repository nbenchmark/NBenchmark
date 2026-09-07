using NBenchmark.Integration.Abstractions;

namespace NBenchmark.Integration.MSTest;

/// <summary>The thresholds a <see cref="PerformanceAssert" /> call gates a measurement against.</summary>
public sealed class PerformanceAssertionOptions : IPerformanceThresholds
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
    ///     Fails the assertion when the measurement was taken in the test host rather than in a worker
    ///     process. Defaults to <c>true</c>. See <see cref="IPerformanceThresholds.RequireIsolation" />.
    /// </summary>
    /// <remarks>
    ///     Settable here, unlike on the attributes, because the <c>PerformanceAssert</c> pattern has no
    ///     attribute target for <c>[AllowInProcessGate]</c> to sit on - the caller has already measured,
    ///     and the gate call is not tied to a method the gate can inspect. This is that pattern's
    ///     opt-out, and being a plain object rather than attribute metadata, <c>false</c> here means
    ///     <c>false</c>.
    /// </remarks>
    public bool RequireIsolation { get; init; } = true;
}
