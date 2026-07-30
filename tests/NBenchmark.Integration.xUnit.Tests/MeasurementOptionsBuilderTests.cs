using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

/// <summary>
///     The translation from a <c>[Performance]</c> attribute to the engine's own options, shared by all
///     three test-framework integrations.
/// </summary>
/// <remarks>
///     <c>LaunchCount</c> is the one that has to arrive: NUnit and MSTest reach the measurement through
///     this builder with no parser in between, so a field dropped here is a test that quietly asks for
///     replicates and gets one launch - with a ratio gate that keeps comparing unpaired numbers and
///     nothing in the output saying it did.
/// </remarks>
public sealed class MeasurementOptionsBuilderTests
{
    [Fact]
    public void LaunchCount_Reaches_The_Options()
        => Assert.Equal(3, MeasurementOptionsBuilder.Build(new Thresholds { LaunchCount = 3 }).LaunchCount);

    [Fact]
    public void LaunchCount_Defaults_To_One()
        => Assert.Equal(1, MeasurementOptionsBuilder.Build(new Thresholds()).LaunchCount);

    /// <summary>
    ///     An out-of-range value is clamped rather than thrown on. An attribute argument is a compile-time
    ///     constant, and failing the test with a configuration error instead of measuring it is a worse
    ///     answer than measuring it the closest valid way.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(-7, 1)]
    [InlineData(10_000, MeasurementOptions.MaxLaunchCount)]
    public void An_Out_Of_Range_LaunchCount_Is_Clamped(int declared, int expected)
        => Assert.Equal(
            expected,
            MeasurementOptionsBuilder.Build(new Thresholds { LaunchCount = declared }).LaunchCount);

    private sealed class Thresholds : IPerformanceThresholds
    {
        public double MaxMeanNs => -1;
        public double MaxP95Ns => -1;
        public long MaxAllocatedBytes => -1;
        public string? ReferenceMethod => null;
        public double MaxSlowdownRatio => 0;
        public int Iterations => 0;
        public int WarmupIterations => 0;
        public bool MeasureAllocations => false;
        public OutlierMode OutlierMode => OutlierMode.IqrFence;
        public double ConfidenceLevel => 0.95;
        public double MaxAbsoluteThresholdTolerance => 1.0;
        public int LaunchCount { get; init; } = 1;
    }
}
