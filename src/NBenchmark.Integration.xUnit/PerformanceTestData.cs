using NBenchmark.Integration.Abstractions;
using Xunit.Abstractions;

namespace NBenchmark.Integration.xUnit;

public sealed class PerformanceTestData : IXunitSerializable, IPerformanceThresholds
{
    private const string NullSentinel = "\0";

    [Obsolete("Called by the deserializer", true)]
    public PerformanceTestData()
    {
    }

    internal PerformanceTestData(
        double maxMeanNs,
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
    public double MaxMeanNs { get; private set; } = -1;
    public double MaxP95Ns { get; private set; } = -1;
    public long MaxAllocatedBytes { get; private set; } = -1;
    public string? ReferenceMethod { get; private set; }
    public double MaxSlowdownRatio { get; private set; }
    public int Samples { get; private set; }
    public int WarmupSamples { get; private set; }
    public bool MeasureAllocations { get; private set; }
    public OutlierMode OutlierMode { get; private set; } = OutlierMode.IqrFence;
    public double ConfidenceLevel { get; private set; } = 0.95;
    public double MaxAbsoluteThresholdTolerance { get; private set; } = 1.0;
    public bool RequireIsolation { get; private set; }
    public int LaunchCount { get; private set; } = 1;

    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(MaxMeanNs), MaxMeanNs);
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

    public void Deserialize(IXunitSerializationInfo info)
    {
        MaxMeanNs = info.GetValue<double>(nameof(MaxMeanNs));
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
