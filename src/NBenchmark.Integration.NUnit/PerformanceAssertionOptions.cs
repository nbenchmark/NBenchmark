using NBenchmark.Integration.Abstractions;

namespace NBenchmark.Integration.NUnit;

public sealed class PerformanceAssertionOptions : IPerformanceThresholds
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
