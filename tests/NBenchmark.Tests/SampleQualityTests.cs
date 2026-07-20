using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Tests for the post-hoc i.i.d. sanity checks (drift via split-half Mann-Whitney,
///     dependence via lag-1 autocorrelation). Fixtures are fully deterministic; the noise
///     generator is a fixed LCG mirrored in the SciPy reference used to choose the parameters,
///     so the split of "drift only / autocorr only / neither" is pinned, not incidental.
/// </summary>
public class SampleQualityTests
{
    // A ramp's lag-1 autocorrelation is exactly 0.7 (pins the coefficient formula).
    [Fact]
    public void Lag1Autocorrelation_Ramp_Is_Point_Seven()
    {
        var ramp = Enumerable.Range(1, 10).Select(i => (double)i).ToArray();

        Assert.Equal(0.7, SampleQuality.Lag1Autocorrelation(ramp), 12);
    }

    [Fact]
    public void WhiteNoise_Triggers_Neither_Warning()
    {
        var samples = WhiteNoise(100, 12345);

        var warnings = SampleQuality.BuildWarnings(samples);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Ar1_Process_Triggers_Autocorrelation_Only()
    {
        // AR(1) with phi = 0.7: lag-1 ~ 0.685, halves statistically indistinguishable.
        var samples = Ar1(100, 0.7, 999);

        Assert.True(SampleQuality.Lag1Autocorrelation(samples) > SampleQuality.AutocorrelationThreshold);

        var warnings = SampleQuality.BuildWarnings(samples);

        Assert.Single(warnings);
        Assert.Contains("autocorrelation", warnings[0]);
    }

    [Fact]
    public void StepChange_Triggers_Drift_Only()
    {
        // Two levels (100 -> 120) with a large alternation that drives within-half lag-1 near
        // zero, so only the split-half drift check fires.
        var samples = LevelShift(100, baseLo: 100, baseHi: 120, amplitude: 9);

        Assert.True(SampleQuality.Lag1Autocorrelation(samples) < SampleQuality.AutocorrelationThreshold);

        var warnings = SampleQuality.BuildWarnings(samples);

        Assert.Single(warnings);
        Assert.Contains("drifted", warnings[0]);
    }

    [Fact]
    public void Below_Minimum_Sample_Count_Skips_Checks()
    {
        var samples = LevelShift(SampleQuality.MinSamplesForChecks - 2, 100, 120, 9);

        Assert.Empty(SampleQuality.BuildWarnings(samples));
    }

    // ---- Deterministic fixtures -------------------------------------------

    private static double[] WhiteNoise(int n, ulong seed)
    {
        var rng = new Lcg(seed);
        var x = new double[n];

        for (var i = 0; i < n; i++)
        {
            x[i] = 100 + 10 * rng.NextUnit();
        }

        return x;
    }

    private static double[] Ar1(int n, double phi, ulong seed)
    {
        var rng = new Lcg(seed);
        var total = n + 100; // burn-in so the process reaches its stationary regime.
        var buf = new double[total];
        var prev = 0.0;

        for (var i = 0; i < total; i++)
        {
            prev = phi * prev + rng.NextUnit();
            buf[i] = prev;
        }

        var x = new double[n];

        for (var i = 0; i < n; i++)
        {
            x[i] = 100 + 10 * buf[i + 100];
        }

        return x;
    }

    private static double[] LevelShift(int n, double baseLo, double baseHi, double amplitude)
    {
        var x = new double[n];
        var half = n / 2;

        for (var i = 0; i < n; i++)
        {
            var level = i < half ? baseLo : baseHi;
            x[i] = level + amplitude * (i % 2 == 0 ? 1 : -1);
        }

        return x;
    }

    /// <summary>A small deterministic LCG matching the SciPy reference used to pick fixtures.</summary>
    private sealed class Lcg(ulong seed)
    {
        private ulong _state = seed;

        public double NextUnit()
        {
            unchecked
            {
                _state = _state * 6364136223846793005UL + 1442695040888963407UL;
            }

            return (_state >> 33) / 1073741824.0 - 1.0;
        }
    }
}
