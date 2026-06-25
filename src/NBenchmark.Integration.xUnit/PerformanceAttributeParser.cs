using NBenchmark.Integration.Abstractions;
using Xunit.Abstractions;

namespace NBenchmark.Integration.xUnit;

internal static class PerformanceAttributeParser
{
    private const double UnsetDouble = -1;
    private const long UnsetLong = -1;
    private const double DefaultMaxSlowdownRatio = 1.2;
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
            BaselinePath = NormalizeBaselinePath(ParseString(attribute, nameof(PerformanceFactAttribute.BaselinePath))),
            MaxSlowdownRatio = NormalizeSlowdownRatio(ParseDouble(attribute, nameof(PerformanceFactAttribute.MaxSlowdownRatio))),
            Iterations = NormalizeIterations(ParseInt(attribute, nameof(PerformanceFactAttribute.Iterations))),
            WarmupIterations = NormalizeIterations(ParseInt(attribute, nameof(PerformanceFactAttribute.WarmupIterations))),
            MeasureAllocations = ParseBool(attribute, nameof(PerformanceFactAttribute.MeasureAllocations)),
            OutlierMode = NormalizeOutlierMode(ParseOutlierMode(attribute), true),
            ConfidenceLevel = NormalizeConfidenceLevel(ParseDouble(attribute, nameof(PerformanceFactAttribute.ConfidenceLevel))),
            MaxAbsoluteThresholdTolerance = NormalizeTolerance(ParseDouble(attribute, nameof(PerformanceFactAttribute.MaxAbsoluteThresholdTolerance))),
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
            BaselinePath = NormalizeBaselinePath(runtime.BaselinePath),
            MaxSlowdownRatio = NormalizeSlowdownRatio(runtime.MaxSlowdownRatio),
            Iterations = NormalizeIterations(runtime.Iterations),
            WarmupIterations = NormalizeIterations(runtime.WarmupIterations),
            MeasureAllocations = runtime.MeasureAllocations,
            OutlierMode = NormalizeOutlierMode(runtime.OutlierMode, false),
            ConfidenceLevel = NormalizeConfidenceLevel(runtime.ConfidenceLevel),
            MaxAbsoluteThresholdTolerance = NormalizeTolerance(runtime.MaxAbsoluteThresholdTolerance),
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

    private static double NormalizeThreshold(double value) => value > 0 ? value : UnsetDouble;

    private static long NormalizeThreshold(long value) => value > 0 ? value : UnsetLong;

    private static string? NormalizeBaselinePath(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int NormalizeIterations(int value) => value > 0 ? value : 0;

    private static double NormalizeSlowdownRatio(double value) => value > 0 ? value : DefaultMaxSlowdownRatio;

    private static double NormalizeConfidenceLevel(double value) => value is > 0 and <= 1 ? value : DefaultConfidenceLevel;

    private static double NormalizeTolerance(double value) => value > 0 ? value : 1.0;

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
        public double MaxMeanNs { get; init; } = -1;
        public double MaxP95Ns { get; init; } = -1;
        public long MaxAllocatedBytes { get; init; } = -1;
        public string? BaselinePath { get; init; }
        public double MaxSlowdownRatio { get; init; } = 1.2;
        public int Iterations { get; init; }
        public int WarmupIterations { get; init; }
        public bool MeasureAllocations { get; init; }
        public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;
        public double ConfidenceLevel { get; init; } = 0.95;
        public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;
    }
}
