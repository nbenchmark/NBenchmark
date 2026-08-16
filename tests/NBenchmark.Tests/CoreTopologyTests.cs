using NBenchmark.Interop;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     The performance/efficiency core split. Only macOS reports one today, so the assertions
///     split into "the platform tells us and the answer has to be coherent" and "the platform does
///     not, and the answer has to be *unknown* rather than a fabricated zero-core machine".
/// </summary>
public class CoreTopologyTests
{
    [Fact]
    public void Read_Reports_Unknown_Off_MacOS()
    {
        if (OperatingSystem.IsMacOS())
            return;

        Assert.Equal((0, 0), CoreTopology.Read());
    }

    [Fact]
    public void Read_Is_Coherent_With_The_Processor_Count()
    {
        var (performance, efficiency) = CoreTopology.Read();

        Assert.True(performance >= 0);
        Assert.True(efficiency >= 0);

        // Zero means unknown, and an unknown split says nothing about the total. A known one has
        // to add up to no more than the logical CPUs the runtime can see.
        if (performance > 0)
            Assert.True(performance + efficiency <= Environment.ProcessorCount);
    }

    [Fact]
    public void Read_Reports_A_Split_On_Apple_Silicon()
    {
        if (!OperatingSystem.IsMacOS() || !System.Runtime.Intrinsics.Arm.ArmBase.IsSupported)
            return;

        var (performance, efficiency) = CoreTopology.Read();

        // Every shipped Apple Silicon part has both core types. An Intel Mac has one performance
        // level and correctly reports unknown, which is why the check is gated on the
        // architecture rather than on the operating system.
        Assert.True(performance > 0, "expected a performance-core count on Apple Silicon");
        Assert.True(efficiency > 0, "expected an efficiency-core count on Apple Silicon");
    }

    [Fact]
    public void Cached_Properties_Agree_With_A_Direct_Read()
    {
        var (performance, efficiency) = CoreTopology.Read();

        Assert.Equal(performance, CoreTopology.PerformanceCoreCount);
        Assert.Equal(efficiency, CoreTopology.EfficiencyCoreCount);
    }
}
