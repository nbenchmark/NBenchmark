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
        int iterations,
        int warmupIterations,
        bool measureAllocations,
        OutlierMode outlierMode,
        double confidenceLevel,
        double maxAbsoluteThresholdTolerance,
        bool requireIsolation = false,
        string? skipReason = null)
    {
        MaxMeanNs = maxMeanNs;
        MaxP95Ns = maxP95Ns;
        MaxAllocatedBytes = maxAllocatedBytes;
        ReferenceMethod = referenceMethod;
        MaxSlowdownRatio = maxSlowdownRatio;
        Iterations = iterations;
        WarmupIterations = warmupIterations;
        MeasureAllocations = measureAllocations;
        OutlierMode = outlierMode;
        ConfidenceLevel = confidenceLevel;
        MaxAbsoluteThresholdTolerance = maxAbsoluteThresholdTolerance;
        RequireIsolation = requireIsolation;
        SkipReason = skipReason;
    }

    internal string? SkipReason { get; private set; }
    public double MaxMeanNs { get; private set; } = -1;
    public double MaxP95Ns { get; private set; } = -1;
    public long MaxAllocatedBytes { get; private set; } = -1;
    public string? ReferenceMethod { get; private set; }
    public double MaxSlowdownRatio { get; private set; }
    public int Iterations { get; private set; }
    public int WarmupIterations { get; private set; }
    public bool MeasureAllocations { get; private set; }
    public OutlierMode OutlierMode { get; private set; } = OutlierMode.IqrFence;
    public double ConfidenceLevel { get; private set; } = 0.95;
    public double MaxAbsoluteThresholdTolerance { get; private set; } = 1.0;
    public bool RequireIsolation { get; private set; }

    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(MaxMeanNs), MaxMeanNs);
        info.AddValue(nameof(MaxP95Ns), MaxP95Ns);
        info.AddValue(nameof(MaxAllocatedBytes), MaxAllocatedBytes);
        info.AddValue(nameof(ReferenceMethod), ReferenceMethod ?? NullSentinel);
        info.AddValue(nameof(MaxSlowdownRatio), MaxSlowdownRatio);
        info.AddValue(nameof(Iterations), Iterations);
        info.AddValue(nameof(WarmupIterations), WarmupIterations);
        info.AddValue(nameof(MeasureAllocations), MeasureAllocations);
        info.AddValue(nameof(OutlierMode), (int)OutlierMode);
        info.AddValue(nameof(ConfidenceLevel), ConfidenceLevel);
        info.AddValue(nameof(MaxAbsoluteThresholdTolerance), MaxAbsoluteThresholdTolerance);
        info.AddValue(nameof(RequireIsolation), RequireIsolation);
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
        Iterations = info.GetValue<int>(nameof(Iterations));
        WarmupIterations = info.GetValue<int>(nameof(WarmupIterations));
        MeasureAllocations = info.GetValue<bool>(nameof(MeasureAllocations));
        OutlierMode = (OutlierMode)info.GetValue<int>(nameof(OutlierMode));
        ConfidenceLevel = info.GetValue<double>(nameof(ConfidenceLevel));
        MaxAbsoluteThresholdTolerance = info.GetValue<double>(nameof(MaxAbsoluteThresholdTolerance));
        RequireIsolation = info.GetValue<bool>(nameof(RequireIsolation));
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
            thresholds.Iterations,
            thresholds.WarmupIterations,
            thresholds.MeasureAllocations,
            thresholds.OutlierMode,
            thresholds.ConfidenceLevel,
            thresholds.MaxAbsoluteThresholdTolerance,
            thresholds.RequireIsolation,
            skipReason);
}
