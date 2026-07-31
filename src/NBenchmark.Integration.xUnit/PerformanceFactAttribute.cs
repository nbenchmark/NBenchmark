using NBenchmark.Integration.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace NBenchmark.Integration.xUnit;

[XunitTestCaseDiscoverer(
    "NBenchmark.Integration.xUnit.PerformanceFactDiscoverer",
    "NBenchmark.Integration.xUnit")]
public sealed class PerformanceFactAttribute : FactAttribute, IPerformanceThresholds
{
    public double MaxMeanNs { get; init; } = -1;
    public double MaxP95Ns { get; init; } = -1;
    public long MaxAllocatedBytes { get; init; } = -1;
    public string? ReferenceMethod { get; init; }
    public double MaxSlowdownRatio { get; init; } = 0;
    public int Iterations { get; init; }
    public int WarmupIterations { get; init; }
    public bool MeasureAllocations { get; init; }
    public OutlierMode OutlierMode { get; init; } = OutlierMode.IqrFence;
    public double ConfidenceLevel { get; init; } = 0.95;
    public double MaxAbsoluteThresholdTolerance { get; init; } = 1.0;

    /// <summary>
    ///     Worker processes to measure this test in. Defaults to 1; two or more give the ratio gate a
    ///     paired confidence interval. See <see cref="IPerformanceThresholds.LaunchCount" />.
    /// </summary>
    public int LaunchCount { get; init; } = 1;

    // No RequireIsolation property. It defaults to true via IPerformanceThresholds, and the opt-out is
    // [AllowInProcessGate]. A settable bool here could not express `false`: xUnit reads attribute values
    // as named arguments, where absent and explicit-false are indistinguishable and Nullable<bool> is
    // not a legal attribute argument type - so the property existed only to be silently ignored.
}
