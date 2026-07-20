using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class WarmupGatesTests
{
    // deactivate = 4 x floor, mirroring the detector's wiring.
    private const double Floor = 1_000.0;
    private const double Deactivate = 4_000.0;

    [Fact]
    public void Blocks_Before_Time_Floor()
    {
        // Below the floor: cannot settle regardless of JIT state.
        Assert.False(WarmupGates.CanSettle(
            warmupElapsedNs: 999.0, minWarmupTimeNs: Floor, jitCompiledDeltaLastBatch: 0,
            requireJitQuiescence: true, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Allows_At_Time_Floor_When_Jit_Quiet()
    {
        // Floor met (>=) and JIT quiet (delta 0): settle.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 1_000.0, minWarmupTimeNs: Floor, jitCompiledDeltaLastBatch: 0,
            requireJitQuiescence: true, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Blocks_While_Jit_Active_Within_Deactivation_Window()
    {
        // Floor met but the JIT is still compiling and we are before the deactivation threshold.
        Assert.False(WarmupGates.CanSettle(
            warmupElapsedNs: 2_000.0, minWarmupTimeNs: Floor, jitCompiledDeltaLastBatch: 3,
            requireJitQuiescence: true, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Allows_While_Jit_Active_Past_Deactivation_Window()
    {
        // Past the deactivation threshold the JIT gate is ignored even though the JIT is still busy.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 4_000.0, minWarmupTimeNs: Floor, jitCompiledDeltaLastBatch: 3,
            requireJitQuiescence: true, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Jit_Gate_Off_Ignores_Jit_Delta()
    {
        // With the gate disabled, only the time floor matters - a positive delta does not block.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 1_000.0, minWarmupTimeNs: Floor, jitCompiledDeltaLastBatch: 99,
            requireJitQuiescence: false, jitGateDeactivateNs: Deactivate));
    }

    [Fact]
    public void Zero_Floor_Disables_Both_Gates()
    {
        // A zero floor makes the deactivation threshold zero too, so the JIT gate never applies and
        // settling is always allowed (the plateau rule alone governs) - even with the JIT busy.
        Assert.True(WarmupGates.CanSettle(
            warmupElapsedNs: 0.0, minWarmupTimeNs: 0.0, jitCompiledDeltaLastBatch: 99,
            requireJitQuiescence: true, jitGateDeactivateNs: 0.0));
    }
}
