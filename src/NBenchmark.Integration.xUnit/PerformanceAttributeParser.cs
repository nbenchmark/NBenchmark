using NBenchmark.Integration.Abstractions;
using Xunit.Abstractions;

namespace NBenchmark.Integration.xUnit;

internal static class PerformanceAttributeParser
{
    private const double DefaultConfidenceLevel = 0.95;

    internal static IPerformanceThresholds Parse(IAttributeInfo attribute)
    {
        if (TryGetRuntimeThresholds(attribute, out var runtimeThresholds))
            return runtimeThresholds;

        return new ParsedThresholds
        {
            MaxMeanNs = NormalizeThreshold(ParseDouble(attribute, nameof(PerformanceFactAttribute.MaxMeanNs))),
            MaxP95Ns = NormalizeThreshold(ParseDouble(attribute, nameof(PerformanceFactAttribute.MaxP95Ns))),
            MaxAllocatedBytes = NormalizeThreshold(ParseLong(attribute, nameof(PerformanceFactAttribute.MaxAllocatedBytes))),
            ReferenceMethod = NormalizeReferenceMethod(ParseString(attribute, nameof(PerformanceFactAttribute.ReferenceMethod))),
            MaxSlowdownRatio = NormalizeSlowdownRatio(ParseDouble(attribute, nameof(PerformanceFactAttribute.MaxSlowdownRatio))),
            Samples = NormalizeSamples(ParseInt(attribute, nameof(PerformanceFactAttribute.Samples))),
            WarmupSamples = NormalizeSamples(ParseInt(attribute, nameof(PerformanceFactAttribute.WarmupSamples))),
            MeasureAllocations = ParseBool(attribute, nameof(PerformanceFactAttribute.MeasureAllocations)),
            OutlierMode = NormalizeOutlierMode(ParseOutlierMode(attribute), true),
            ConfidenceLevel = NormalizeConfidenceLevel(ParseDouble(attribute, nameof(PerformanceFactAttribute.ConfidenceLevel))),
            MaxAbsoluteThresholdTolerance = NormalizeTolerance(ParseDouble(attribute, nameof(PerformanceFactAttribute.MaxAbsoluteThresholdTolerance))),
            // RequireIsolation is deliberately absent. It is not an attribute argument - a named
            // argument cannot distinguish an explicit `false` from an absent one - so it keeps
            // ParsedThresholds' default of true, and [AllowInProcessGate] is the opt-out.
            LaunchCount = NormalizeLaunchCount(ParseInt(attribute, nameof(PerformanceFactAttribute.LaunchCount))),
        };
    }

    private static bool TryGetRuntimeThresholds(IAttributeInfo attribute, out IPerformanceThresholds thresholds)
    {
        thresholds = null!;

        if (attribute is not IReflectionAttributeInfo { Attribute: IPerformanceThresholds runtime })
            return false;

        thresholds = new ParsedThresholds
        {
            MaxMeanNs = NormalizeThreshold(runtime.MaxMeanNs),
            MaxP95Ns = NormalizeThreshold(runtime.MaxP95Ns),
            MaxAllocatedBytes = NormalizeThreshold(runtime.MaxAllocatedBytes),
            ReferenceMethod = NormalizeReferenceMethod(runtime.ReferenceMethod),
            MaxSlowdownRatio = NormalizeSlowdownRatio(runtime.MaxSlowdownRatio),
            Samples = NormalizeSamples(runtime.Samples),
            WarmupSamples = NormalizeSamples(runtime.WarmupSamples),
            MeasureAllocations = runtime.MeasureAllocations,
            OutlierMode = NormalizeOutlierMode(runtime.OutlierMode, false),
            ConfidenceLevel = NormalizeConfidenceLevel(runtime.ConfidenceLevel),
            MaxAbsoluteThresholdTolerance = NormalizeTolerance(runtime.MaxAbsoluteThresholdTolerance),
            RequireIsolation = runtime.RequireIsolation,
            LaunchCount = NormalizeLaunchCount(runtime.LaunchCount),
        };

        return true;
    }

    private static double ParseDouble(IAttributeInfo attribute, string name) => attribute.GetNamedArgument<double>(name);

    private static long ParseLong(IAttributeInfo attribute, string name) => attribute.GetNamedArgument<long>(name);

    private static string? ParseString(IAttributeInfo attribute, string name)
    {
        var raw = attribute.GetNamedArgument<string?>(name);
        return raw;
    }

    private static int ParseInt(IAttributeInfo attribute, string name)
    {
        var raw = attribute.GetNamedArgument<int>(name);
        return raw;
    }

    private static bool ParseBool(IAttributeInfo attribute, string name)
    {
        var raw = attribute.GetNamedArgument<bool>(name);
        return raw;
    }

    private static OutlierMode ParseOutlierMode(IAttributeInfo attribute)
    {
        var raw = attribute.GetNamedArgument<OutlierMode>(nameof(PerformanceFactAttribute.OutlierMode));
        return raw;
    }

    private static double NormalizeThreshold(double value) => value > 0 ? value : IPerformanceThresholds.Unset;

    private static long NormalizeThreshold(long value) => value > 0 ? value : IPerformanceThresholds.UnsetBytes;

    private static string? NormalizeReferenceMethod(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int NormalizeSamples(int value) => value > 0 ? value : IPerformanceThresholds.AutoSampleCount;

    private static double NormalizeSlowdownRatio(double value) => value > 0 ? value : IPerformanceThresholds.Unset;

    private static double NormalizeConfidenceLevel(double value) => value is > 0 and <= 1 ? value : DefaultConfidenceLevel;

    private static double NormalizeTolerance(double value) => value > 0 ? value : 1.0;

    /// <summary>
    ///     One replicate unless the test asked for more. An unset named argument reads as <c>0</c> here,
    ///     which is not a valid launch count - and clamping rather than throwing keeps a mistyped
    ///     attribute from failing the test with a configuration error instead of measuring it.
    /// </summary>
    private static int NormalizeLaunchCount(int value) => LaunchCounts.Clamp(value);

    private static OutlierMode NormalizeOutlierMode(OutlierMode value, bool treatNoneAsUnset)
    {
        if (treatNoneAsUnset && value == OutlierMode.None)
            return OutlierMode.IqrFence;

        return value is OutlierMode.None
            or OutlierMode.RemoveTop5Percent
            or OutlierMode.RemoveTopAndBottom5Percent
            or OutlierMode.IqrFence
            or OutlierMode.MedianAbsoluteDeviation
            ? value
            : OutlierMode.IqrFence;
    }

    private sealed class ParsedThresholds : IPerformanceThresholds
    {
        public double MaxMeanNs { get; init; } = IPerformanceThresholds.Unset;
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
        public bool RequireIsolation { get; init; } = true;
        public int LaunchCount { get; init; } = 1;
    }
}
