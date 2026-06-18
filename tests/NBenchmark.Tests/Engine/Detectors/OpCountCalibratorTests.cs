using NBenchmark.Engine.Detectors;
using Xunit;

namespace NBenchmark.Tests;

public class OpCountCalibratorTests
{
    private static int Calibrate(double targetNs, int maxOps, Func<int, double> sampleNs)
    {
        var calibrator = new OpCountCalibrator(targetNs, maxOps);

        // Drive the doubling search: time a sample at the current K, feed it back, repeat
        // until resolved. Guard against a runaway loop in case of a regression.
        for (var guard = 0; guard < 1_000; guard++)
        {
            if (calibrator.Feed(sampleNs(calibrator.OpsPerSample)))
                return calibrator.OpsPerSample;
        }

        throw new InvalidOperationException("Calibrator did not resolve.");
    }

    [Fact]
    public void SlowBody_ResolvesToOne()
    {
        // The very first sample (K = 1) already exceeds the target, so no batching is needed.
        var k = Calibrate(1_000, 1 << 20, _ => 1_500);

        Assert.Equal(1, k);
    }

    [Fact]
    public void FastBody_DoublesUntilTarget()
    {
        // Each op costs ~50 ns, so a sample spans K * 50 ns. The search doubles K until
        // K * 50 >= 1000, i.e. K = 32 (16 * 50 = 800 < 1000; 32 * 50 = 1600 >= 1000).
        var k = Calibrate(1_000, 1 << 20, ops => ops * 50.0);

        Assert.Equal(32, k);
    }

    [Fact]
    public void VeryFastBody_CapsAtMaxOpsPerSample()
    {
        // A near-instant body never reaches the target, so calibration stops at the ceiling.
        var k = Calibrate(1_000, 16, _ => 1.0);

        Assert.Equal(16, k);
    }

    [Theory]
    [InlineData(400, 4)]
    [InlineData(1_000, 16)]
    [InlineData(4_000, 64)]
    public void TargetDurationHonoured(double targetNs, int expectedK)
    {
        // Each op costs 100 ns; the search resolves at the smallest power-of-two K whose
        // sample (K * 100 ns) meets the target.
        var k = Calibrate(targetNs, 1 << 20, ops => ops * 100.0);

        Assert.Equal(expectedK, k);
    }
}
