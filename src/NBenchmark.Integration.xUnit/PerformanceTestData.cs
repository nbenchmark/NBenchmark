using NBenchmark.Integration.Abstractions;
using Xunit.Abstractions;

namespace NBenchmark.Integration.xUnit;

/// <summary>
///     The performance thresholds for one <see cref="PerformanceTestCase" />, carried across xUnit's
///     discovery/execution serialization boundary since the attribute instance itself does not survive it.
/// </summary>
public sealed class PerformanceTestData : IXunitSerializable, IPerformanceThresholds
{
    private const string NullSentinel = "\0";

    /// <summary>Deserialization constructor. Do not call directly; see <see cref="Deserialize" />.</summary>
    [Obsolete("Called by the deserializer", true)]
    public PerformanceTestData()
    {
    }

    internal PerformanceTestData(
        double maxMeanNs,
        double maxMedianNs,
        double maxP95Ns,
        long maxAllocatedBytes,
        string? referenceMethod,
        double maxSlowdownRatio,
        int samples,
        int warmupSamples,
        bool measureAllocations,
        OutlierMode outlierMode,
        double confidenceLevel,
        double maxAbsoluteThresholdTolerance,
        bool requireIsolation = false,
        int launchCount = 1,
        string? skipReason = null)
    {
        MaxMeanNs = maxMeanNs;
        MaxMedianNs = maxMedianNs;
        MaxP95Ns = maxP95Ns;
        MaxAllocatedBytes = maxAllocatedBytes;
        ReferenceMethod = referenceMethod;
        MaxSlowdownRatio = maxSlowdownRatio;
        Samples = samples;
        WarmupSamples = warmupSamples;
        MeasureAllocations = measureAllocations;
        OutlierMode = outlierMode;
        ConfidenceLevel = confidenceLevel;
        MaxAbsoluteThresholdTolerance = maxAbsoluteThresholdTolerance;
        RequireIsolation = requireIsolation;
        LaunchCount = launchCount;
        SkipReason = skipReason;
    }

    internal string? SkipReason { get; private set; }

    /// <summary>Maximum mean time per operation in nanoseconds, or <see cref="IPerformanceThresholds.Unset" />.</summary>
    public double MaxMeanNs { get; private set; } = -1;

    /// <summary>
    ///     Maximum median time per operation in nanoseconds, or <see cref="IPerformanceThresholds.Unset" />.
    ///     See <see cref="IPerformanceThresholds.MaxMedianNs" />.
    /// </summary>
    public double MaxMedianNs { get; private set; } = -1;

    /// <summary>Maximum 95th-percentile time per operation in nanoseconds, or <see cref="IPerformanceThresholds.Unset" />.</summary>
    public double MaxP95Ns { get; private set; } = -1;

    /// <summary>Maximum mean bytes allocated per operation, or <see cref="IPerformanceThresholds.UnsetBytes" />.</summary>
    public long MaxAllocatedBytes { get; private set; } = -1;

    /// <summary>
    ///     The name of the method to measure alongside this one as the denominator of
    ///     <see cref="MaxSlowdownRatio" />, or <c>null</c> when there is no comparison.
    /// </summary>
    public string? ReferenceMethod { get; private set; }

    /// <summary>
    ///     Maximum ratio of this measurement to <see cref="ReferenceMethod" />'s, or
    ///     <see cref="IPerformanceThresholds.Unset" />.
    /// </summary>
    public double MaxSlowdownRatio { get; private set; }

    /// <summary>Measured samples to take, or <see cref="IPerformanceThresholds.AutoSampleCount" />.</summary>
    public int Samples { get; private set; }

    /// <summary>Warmup samples to take before measuring, or <see cref="IPerformanceThresholds.AutoSampleCount" />.</summary>
    public int WarmupSamples { get; private set; }

    /// <summary>Whether to measure allocations as well as time.</summary>
    public bool MeasureAllocations { get; private set; }

    /// <summary>Which samples to trim before the statistics are computed.</summary>
    public OutlierMode OutlierMode { get; private set; } = OutlierMode.IqrFence;

    /// <summary>The confidence level for the reported interval, e.g. <c>0.95</c>.</summary>
    public double ConfidenceLevel { get; private set; } = 0.95;

    /// <summary>
    ///     How far past an absolute threshold a measurement may land before the gate fails, as a
    ///     multiplier of the threshold. <c>1.0</c> fails at the threshold exactly.
    /// </summary>
    public double MaxAbsoluteThresholdTolerance { get; private set; } = 1.0;

    /// <summary>See <see cref="IPerformanceThresholds.RequireIsolation" />.</summary>
    public bool RequireIsolation { get; private set; }

    /// <summary>See <see cref="IPerformanceThresholds.LaunchCount" />.</summary>
    public int LaunchCount { get; private set; } = 1;

    /// <summary>Writes every threshold to the serialization info, for the discovery/execution round trip.</summary>
    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(MaxMeanNs), MaxMeanNs);
        info.AddValue(nameof(MaxMedianNs), MaxMedianNs);
        info.AddValue(nameof(MaxP95Ns), MaxP95Ns);
        info.AddValue(nameof(MaxAllocatedBytes), MaxAllocatedBytes);
        info.AddValue(nameof(ReferenceMethod), ReferenceMethod ?? NullSentinel);
        info.AddValue(nameof(MaxSlowdownRatio), MaxSlowdownRatio);
        info.AddValue(nameof(Samples), Samples);
        info.AddValue(nameof(WarmupSamples), WarmupSamples);
        info.AddValue(nameof(MeasureAllocations), MeasureAllocations);
        info.AddValue(nameof(OutlierMode), (int)OutlierMode);
        info.AddValue(nameof(ConfidenceLevel), ConfidenceLevel);
        info.AddValue(nameof(MaxAbsoluteThresholdTolerance), MaxAbsoluteThresholdTolerance);
        info.AddValue(nameof(RequireIsolation), RequireIsolation);
        info.AddValue(nameof(LaunchCount), LaunchCount);
        info.AddValue(nameof(SkipReason), SkipReason ?? NullSentinel);
    }

    /// <summary>Restores every threshold from the serialization info, defaulting fields an older build never wrote.</summary>
    public void Deserialize(IXunitSerializationInfo info)
    {
        MaxMeanNs = info.GetValue<double>(nameof(MaxMeanNs));

        // Defaulted rather than trusted, for the reason LaunchCount is below: a test case serialized
        // by a build that predates this threshold carries no value, and the 0 that reads back is a
        // threshold of zero nanoseconds rather than the absent one it really is.
        var maxMedianNs = info.GetValue<double>(nameof(MaxMedianNs));
        MaxMedianNs = maxMedianNs > 0 ? maxMedianNs : IPerformanceThresholds.Unset;
        MaxP95Ns = info.GetValue<double>(nameof(MaxP95Ns));
        MaxAllocatedBytes = info.GetValue<long>(nameof(MaxAllocatedBytes));
        var referenceMethod = info.GetValue<string>(nameof(ReferenceMethod));
        ReferenceMethod = referenceMethod == NullSentinel ? null : referenceMethod;
        MaxSlowdownRatio = info.GetValue<double>(nameof(MaxSlowdownRatio));
        Samples = info.GetValue<int>(nameof(Samples));
        WarmupSamples = info.GetValue<int>(nameof(WarmupSamples));
        MeasureAllocations = info.GetValue<bool>(nameof(MeasureAllocations));
        OutlierMode = (OutlierMode)info.GetValue<int>(nameof(OutlierMode));
        ConfidenceLevel = info.GetValue<double>(nameof(ConfidenceLevel));
        MaxAbsoluteThresholdTolerance = info.GetValue<double>(nameof(MaxAbsoluteThresholdTolerance));
        RequireIsolation = info.GetValue<bool>(nameof(RequireIsolation));

        // Defaulted rather than trusted: a test case serialized by an older build carries no value,
        // and a 0 here would be an invalid replicate count rather than the absent one it really is.
        var launchCount = info.GetValue<int>(nameof(LaunchCount));
        LaunchCount = launchCount < 1 ? 1 : launchCount;
        var skipReason = info.GetValue<string>(nameof(SkipReason));
        SkipReason = skipReason == NullSentinel ? null : skipReason;
    }

    internal static PerformanceTestData FromThresholds(IPerformanceThresholds thresholds, string? skipReason = null) =>
        new(
            thresholds.MaxMeanNs,
            thresholds.MaxMedianNs,
            thresholds.MaxP95Ns,
            thresholds.MaxAllocatedBytes,
            thresholds.ReferenceMethod,
            thresholds.MaxSlowdownRatio,
            thresholds.Samples,
            thresholds.WarmupSamples,
            thresholds.MeasureAllocations,
            thresholds.OutlierMode,
            thresholds.ConfidenceLevel,
            thresholds.MaxAbsoluteThresholdTolerance,
            thresholds.RequireIsolation,
            thresholds.LaunchCount,
            skipReason);
}
