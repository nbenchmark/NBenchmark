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
    public void Read_Is_Coherent()
    {
        var (performance, efficiency) = CoreTopology.Read();

        Assert.True(performance >= 0);
        Assert.True(efficiency >= 0);

        // A reported split names both kinds of core; zero performance cores means unknown, and an
        // unknown split cannot carry an efficiency count either.
        if (performance == 0)
            Assert.Equal(0, efficiency);

        // Deliberately not compared against ProcessorCount. These counts come from sysctl and
        // describe the machine, while ProcessorCount describes what this process is permitted to
        // use - a cgroup CPU quota or DOTNET_PROCESSOR_COUNT caps the second without touching the
        // first, so a sum above ProcessorCount is a legitimate state rather than a contradiction.
    }

    [Fact]
    public void Read_Reports_A_Split_On_Apple_Silicon()
    {
        if (!OperatingSystem.IsMacOS() || !System.Runtime.Intrinsics.Arm.ArmBase.IsSupported)
            return;

        var (performance, efficiency) = CoreTopology.Read();

        // Every shipped Apple Silicon *part* has both core types, but running on one is not the
        // same as being able to see it: a virtualised host - the macOS CI runner among them - is
        // handed a slice of CPUs with no hw.nperflevels behind it, and unknown is then the honest
        // answer rather than a failure. So the assertion is the contract, not the hardware. On a
        // Mac running on the metal the second branch is the one that runs.
        if (performance == 0)
        {
            Assert.Equal(0, efficiency);
            return;
        }

        Assert.True(efficiency > 0, "a reported split names both kinds of core");
    }

    [Fact]
    public void Cached_Properties_Agree_With_A_Direct_Read()
    {
        var (performance, efficiency) = CoreTopology.Read();

        Assert.Equal(performance, CoreTopology.PerformanceCoreCount);
        Assert.Equal(efficiency, CoreTopology.EfficiencyCoreCount);
    }
}
