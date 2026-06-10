using NBenchmark.Extensions.Abstractions;
using Xunit.Abstractions;

namespace NBenchmark.Extensions.xUnit;

public sealed class PerformanceTestData : IXunitSerializable
{
    private const string NullSentinel = "\0";
    public double MaxMeanNs { get; private set; } = -1;
    public double MaxP95Ns { get; private set; } = -1;
    public long MaxAllocatedBytes { get; private set; } = -1;
    public string? BaselinePath { get; private set; }
    public double MaxSlowdownRatio { get; private set; } = 1.2;
    public int Iterations { get; private set; }
    public int WarmupIterations { get; private set; }
    public bool MeasureAllocations { get; private set; }
    public OutlierMode OutlierMode { get; private set; } = OutlierMode.RemoveTop5Percent;
    public double ConfidenceLevel { get; private set; } = 0.95;
    internal string? SkipReason { get; private set; }

    [Obsolete("Called by the deserializer", true)]
    public PerformanceTestData() { }

    internal PerformanceTestData(
        double maxMeanNs,
        double maxP95Ns,
        long maxAllocatedBytes,
        string? baselinePath,
        double maxSlowdownRatio,
        int iterations,
        int warmupIterations,
        bool measureAllocations,
        OutlierMode outlierMode,
        double confidenceLevel,
        string? skipReason = null)
    {
        MaxMeanNs = maxMeanNs;
        MaxP95Ns = maxP95Ns;
        MaxAllocatedBytes = maxAllocatedBytes;
        BaselinePath = baselinePath;
        MaxSlowdownRatio = maxSlowdownRatio;
        Iterations = iterations;
        WarmupIterations = warmupIterations;
        MeasureAllocations = measureAllocations;
        OutlierMode = outlierMode;
        ConfidenceLevel = confidenceLevel;
        SkipReason = skipReason;
    }

    public static PerformanceTestData FromThresholds(IPerformanceThresholds thresholds, string? skipReason = null) =>
        new(
            thresholds.MaxMeanNs,
            thresholds.MaxP95Ns,
            thresholds.MaxAllocatedBytes,
            thresholds.BaselinePath,
            thresholds.MaxSlowdownRatio,
            thresholds.Iterations,
            thresholds.WarmupIterations,
            thresholds.MeasureAllocations,
            thresholds.OutlierMode,
            thresholds.ConfidenceLevel,
            skipReason);

    public void Serialize(IXunitSerializationInfo info)
    {
        info.AddValue(nameof(MaxMeanNs), MaxMeanNs);
        info.AddValue(nameof(MaxP95Ns), MaxP95Ns);
        info.AddValue(nameof(MaxAllocatedBytes), MaxAllocatedBytes);
        info.AddValue(nameof(BaselinePath), BaselinePath ?? NullSentinel);
        info.AddValue(nameof(MaxSlowdownRatio), MaxSlowdownRatio);
        info.AddValue(nameof(Iterations), Iterations);
        info.AddValue(nameof(WarmupIterations), WarmupIterations);
        info.AddValue(nameof(MeasureAllocations), MeasureAllocations);
        info.AddValue(nameof(OutlierMode), (int)OutlierMode);
        info.AddValue(nameof(ConfidenceLevel), ConfidenceLevel);
        info.AddValue(nameof(SkipReason), SkipReason ?? NullSentinel);
    }

    public void Deserialize(IXunitSerializationInfo info)
    {
        MaxMeanNs = info.GetValue<double>(nameof(MaxMeanNs));
        MaxP95Ns = info.GetValue<double>(nameof(MaxP95Ns));
        MaxAllocatedBytes = info.GetValue<long>(nameof(MaxAllocatedBytes));
        var baselinePath = info.GetValue<string>(nameof(BaselinePath));
        BaselinePath = baselinePath == NullSentinel ? null : baselinePath;
        MaxSlowdownRatio = info.GetValue<double>(nameof(MaxSlowdownRatio));
        Iterations = info.GetValue<int>(nameof(Iterations));
        WarmupIterations = info.GetValue<int>(nameof(WarmupIterations));
        MeasureAllocations = info.GetValue<bool>(nameof(MeasureAllocations));
        OutlierMode = (OutlierMode)info.GetValue<int>(nameof(OutlierMode));
        ConfidenceLevel = info.GetValue<double>(nameof(ConfidenceLevel));
        var skipReason = info.GetValue<string>(nameof(SkipReason));
        SkipReason = skipReason == NullSentinel ? null : skipReason;
    }
}
