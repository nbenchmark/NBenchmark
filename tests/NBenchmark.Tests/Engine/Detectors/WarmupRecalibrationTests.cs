using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class WarmupRecalibrationTests
{
    private const double Target = 10_000.0;
    private const int MaxOps = 1 << 20;
    private const double Trigger = 0.5;

    [Fact]
    public void Bumps_K_To_Next_Power_Of_Two_When_Warm_Sample_Under_Half_Target()
    {
        // Warm per-op 100 ns at K = 1 -> sample spans 100 ns, far under half the 10 µs target.
        // neededOps = ceil(10000 / 100) = 100 -> next pow2 = 128.
        var newK = WarmupRecalibration.Resolve(currentK: 1, warmPerOpNs: 100.0, Target, MaxOps, Trigger);

        Assert.Equal(128, newK);
    }

    [Fact]
    public void Warm_Ten_Times_Faster_Than_Cold_Bumps_K_By_Sixteen()
    {
        // The plan's worked example: cold calibration resolved K = 16 (cold per-op 625 ns spans the
        // 10 µs target). The warm body runs 10x faster (62.5 ns/op), so the warm sample spans
        // 16 x 62.5 = 1000 ns < 5000 ns -> recalibrate. neededOps = ceil(10000 / 62.5) = 160 ->
        // next pow2 = 256 = 16 x the cold K.
        var newK = WarmupRecalibration.Resolve(currentK: 16, warmPerOpNs: 62.5, Target, MaxOps, Trigger);

        Assert.Equal(256, newK);
    }

    [Fact]
    public void No_Change_When_Warm_Sample_Already_Near_Target()
    {
        // Warm == cold: K = 16 at 625 ns/op spans the full 10 µs target, above the half-target
        // trigger, so no recalibration.
        var newK = WarmupRecalibration.Resolve(currentK: 16, warmPerOpNs: 625.0, Target, MaxOps, Trigger);

        Assert.Equal(16, newK);
    }

    [Fact]
    public void Never_Decreases_K()
    {
        // A warm sample already over the target must not shrink K (the trigger guard prevents it).
        var newK = WarmupRecalibration.Resolve(currentK: 128, warmPerOpNs: 1_000.0, Target, MaxOps, Trigger);

        Assert.Equal(128, newK);
    }

    [Fact]
    public void Clamps_To_MaxOps()
    {
        // An extremely fast warm body would need a huge K; the result is clamped to maxOps.
        var newK = WarmupRecalibration.Resolve(currentK: 1, warmPerOpNs: 0.001, Target, maxOps: 64, Trigger);

        Assert.Equal(64, newK);
    }

    [Theory]
    [InlineData(0, 100.0)]   // currentK < 1
    [InlineData(1, 0.0)]     // warmPerOp <= 0
    [InlineData(1, -5.0)]    // warmPerOp negative
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    public void Returns_CurrentK_On_Invalid_Input(int currentK, double warmPerOpNs)
    {
        var newK = WarmupRecalibration.Resolve(currentK, warmPerOpNs, Target, MaxOps, Trigger);

        Assert.Equal(currentK, newK);
    }
}
