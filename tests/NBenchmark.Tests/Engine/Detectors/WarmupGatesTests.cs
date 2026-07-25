using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class WarmupGatesTests
{
    // deactivate = 4 x floor and quiet <= floor, mirroring the detector's wiring.
    private const double Floor = 1_000.0;
    private const double Quiet = 200.0;
    private const double Deactivate = 4_000.0;

    [Fact]
    public void Blocks_Before_Time_Floor()
    {
        // Below the floor: cannot settle regardless of JIT state.
        Assert.False(WarmupGates.CanSettle(
            warmupElapsedNs: 999.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 0.0,
            requireJitQuiescence: true, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Allows_At_Time_Floor_When_Jit_Never_Compiled()
    {
        // A body that triggers no compilation at all is "quiet since ns 0", so the gate collapses to
        // the time floor - the common case, and it must not cost anything.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 1_000.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 0.0,
            requireJitQuiescence: true, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Allows_At_Time_Floor_When_Quiet_Period_Has_Elapsed()
    {
        // Last change at 700 ns, now at 1000 ns: 300 ns of quiet against a 200 ns requirement.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 1_000.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 700.0,
            requireJitQuiescence: true, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Blocks_When_Jit_Changed_Too_Recently()
    {
        // This is the case the old per-batch delta rule could not see. The JIT last compiled 100 ns
        // ago, short of the 200 ns quiet requirement, so warmup must continue even though the time
        // floor is met and this particular batch saw no compilation.
        Assert.False(WarmupGates.CanSettle(
            warmupElapsedNs: 1_100.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 1_000.0,
            requireJitQuiescence: true, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Allows_Exactly_At_Quiet_Period_Boundary()
    {
        // The comparison is >=, so exactly one quiet period is enough.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 1_200.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 1_000.0,
            requireJitQuiescence: true, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Allows_With_Recent_Jit_Change_Past_Deactivation_Window()
    {
        // Past the deactivation threshold the gate is ignored even though the JIT just compiled, so a
        // busy host that JITs unrelated code cannot hold warmup open forever.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 4_000.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 3_990.0,
            requireJitQuiescence: true, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Jit_Gate_Off_Ignores_Recent_Jit_Change()
    {
        // With the gate disabled, only the time floor matters.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 1_000.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 999.0,
            requireJitQuiescence: false, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Zero_Quiet_Period_Disables_The_Gate()
    {
        // A zero quiet period means the user asked for the time floor alone.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 1_000.0, minWarmupTimeNs: Floor, lastJitChangeAtNs: 1_000.0,
            requireJitQuiescence: true, jitQuietPeriodNs: 0.0, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Zero_Floor_Disables_Both_Gates()
    {
        // A zero floor leaves no timescale to measure a quiet period against, so the JIT gate never
        // applies and the plateau rule alone governs - even with the JIT having just compiled.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 0.0, minWarmupTimeNs: 0.0, lastJitChangeAtNs: 0.0,
            requireJitQuiescence: true, jitQuietPeriodNs: Quiet, jitGateDeactivateNs: 0.0));
    }
}
