using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     External cross-checks: NBenchmark's in-process numerical results are pinned
///     against values pre-computed by SciPy 1.17.1 / NumPy 2.4.6. The reference
///     values and tolerances let the docs claim "matches SciPy/NumPy within …".
///     Reference generation (see docs/advanced/validation.md):
///     mean   = numpy.mean(x)
///     stddev = numpy.std(x, ddof=1)
///     sem    = stddev / sqrt(n)
///     moe    = scipy.stats.t.ppf((1+cl)/2, n-1) * sem
///     pXX    = numpy.percentile(x, XX, method='inverted_cdf')   (nearest-rank)
/// </summary>
public class StatsCrossCheckTests
{
    // ---- Fixtures ----------------------------------------------------------

    private static readonly double[] OneToFive = [1, 2, 3, 4, 5];

    private static readonly double[] Timings =
        [102.3, 98.7, 110.1, 95.4, 103.8, 99.9, 101.2, 97.6, 105.5, 100.0];

    private static readonly double[] Normal64 =
    [
        443.04699854181473, 550.5491383251644, 465.1735304816366, 489.6330706026241,
        496.9862677195792, 470.36461391657565, 445.28829192868227, 525.9557120877216,
        514.4423245221958, 421.8854774795124, 593.896386175154, 538.7398762300769,
        469.6245127830197, 536.08793096849, 481.321873067178, 497.57241925051886,
        531.553773780768, 449.73327467441294, 523.0343005758372, 555.9591597889488,
        552.8919224293114, 488.01205938803577, 536.1167736570023, 435.13669063271175,
        493.6724295729251, 517.979357284267, 446.2559571005442, 496.73249637212666,
        568.9895972926532, 604.7263770547136, 531.0944537524307, 533.1453278226936,
        461.64046747927955, 451.6244685210273, 443.5083194610353, 521.6618731962021,
        530.0775758223075, 473.64958721730187, 450.8530005741889, 510.30231073651487,
        512.516116737388, 494.7675323910624, 550.7993248187173, 496.2815016906687,
        497.3539644399335, 455.6714213162772, 505.43827402202095, 553.8831105718907,
        502.44576083904036, 502.83658401138837, 517.3461814821147, 511.09934639483606,
        521.2100954656048, 521.4688387647478, 524.7340005920331, 468.1993017541332,
        512.0012378514647, 435.89193631345074, 510.67195318973586, 449.53504873114485,
        497.14916775232876, 518.9619892100018, 483.4058495571468, 503.9086600005979,
    ];

    // ---- Descriptive statistics vs NumPy/SciPy -----------------------------

    [Fact]
    public void OneToFive_Matches_Reference()
    {
        var stats = Compute(OneToFive, 0.95);

        Numerics.AssertRelativeClose(3.0, stats.MeanNs, 1e-12);
        Numerics.AssertRelativeClose(1.5811388300841898, stats.StandardDeviationNs, 1e-9);
        Numerics.AssertRelativeClose(0.7071067811865476, stats.StandardErrorNs, 1e-9);
        Numerics.AssertRelativeClose(1.9632431614775572, stats.MarginOfErrorNs, 1e-2);

        Assert.Equal(3.0, stats.MedianNs, 12);
        Assert.Equal(5.0, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value, 12);
        Assert.Equal(5.0, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.99) < 1e-9).Value, 12);
    }

    [Fact]
    public void OneToFive_MarginOfError_99_Matches_Reference()
    {
        var stats = Compute(OneToFive, 0.99);
        Numerics.AssertRelativeClose(3.2555867047577847, stats.MarginOfErrorNs, 1e-2);
    }

    [Fact]
    public void Timings_Matches_Reference()
    {
        var stats = Compute(Timings, 0.95);

        Numerics.AssertRelativeClose(101.45, stats.MeanNs, 1e-12);
        Numerics.AssertRelativeClose(4.229854213405782, stats.StandardDeviationNs, 1e-9);
        Numerics.AssertRelativeClose(1.3375973484822197, stats.StandardErrorNs, 1e-9);
        Numerics.AssertRelativeClose(3.02585542280894, stats.MarginOfErrorNs, 1e-2);

        // n = 10 (even): numpy.median mid-averages the two middles (100.0, 101.2) → 100.6,
        // rather than the nearest-rank P50 of 100.0.
        Assert.Equal(100.6, stats.MedianNs, 12);
        Assert.Equal(110.1, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value, 12);
        Assert.Equal(110.1, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.99) < 1e-9).Value, 12);
    }

    [Fact]
    public void Normal64_Matches_Reference()
    {
        var stats = Compute(Normal64, 0.95);

        Numerics.AssertRelativeClose(501.5077683775768, stats.MeanNs, 1e-9);
        Numerics.AssertRelativeClose(39.289116231983144, stats.StandardDeviationNs, 1e-9);
        Numerics.AssertRelativeClose(4.911139528997893, stats.StandardErrorNs, 1e-9);
        Numerics.AssertRelativeClose(9.814129230772705, stats.MarginOfErrorNs, 1e-3);

        // n = 64 (even): numpy.median = mean of the two middles → 503.3726220059931, versus the
        // nearest-rank P50 of 502.83658401138837.
        Assert.Equal(503.3726220059931, stats.MedianNs, 9);
        Assert.Equal(555.9591597889488, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.95) < 1e-9).Value, 9);
        Assert.Equal(604.7263770547136, stats.Percentiles.FirstOrDefault(e => Math.Abs(e.Percentile - 0.99) < 1e-9).Value, 9);
    }

    // ---- Nearest-rank percentiles vs numpy.percentile(method='inverted_cdf')

    [Fact]
    public void Percentiles_Match_Numpy_InvertedCdf()
    {
        var sorted = (double[])Normal64.Clone();
        Array.Sort(sorted);

        // numpy.percentile(Normal64, q, method='inverted_cdf'). P50 is intentionally omitted:
        // the median uses the mid-average convention (numpy.median), asserted in the *_Matches_Reference
        // tests, so it no longer matches nearest-rank on even n.
        Assert.Equal(469.6245127830197, Percentile.Compute(sorted, 0.25), 9);
        Assert.Equal(524.7340005920331, Percentile.Compute(sorted, 0.75), 9);
        Assert.Equal(555.9591597889488, Percentile.Compute(sorted, 0.95), 9);
        Assert.Equal(604.7263770547136, Percentile.Compute(sorted, 0.99), 9);
    }

    // ---- Student's t critical values vs SciPy ------------------------------

    // df = 1 and df = 2 use exact closed forms and match SciPy to machine precision.
    [Theory]
    [InlineData(0.95, 1, 12.706204736174694)]
    [InlineData(0.99, 1, 63.656741162871526)]
    [InlineData(0.95, 2, 4.302652729749462)]
    [InlineData(0.99, 2, 9.924843200918287)]
    public void TCritical_ExactForms_Match_Scipy(double cl, int df, double expected) =>
        Numerics.AssertRelativeClose(expected, StudentT.CriticalValue(cl, df), 1e-9);

    // df ≥ 3 uses the Cornish-Fisher expansion: documented < 1% (worst case is
    // df = 3 at 99%, ≈ 0.79% relative error). For df ≥ 30 the error is ≪ 0.1%.
    [Theory]
    [InlineData(0.95, 3, 3.1824463052837078, 1e-2)]
    [InlineData(0.99, 3, 5.840909309733355, 1e-2)]
    [InlineData(0.95, 5, 2.5705818356363146, 1e-3)]
    [InlineData(0.95, 10, 2.228138851986274, 1e-3)]
    [InlineData(0.95, 30, 2.0422724563012378, 1e-4)]
    [InlineData(0.99, 30, 2.7499956535672254, 1e-4)]
    [InlineData(0.95, 100, 1.983971518523552, 1e-4)]
    [InlineData(0.95, 1000, 1.9623390808264078, 1e-4)]
    public void TCritical_CornishFisher_Matches_Scipy(double cl, int df, double expected, double relTol) =>
        Numerics.AssertRelativeClose(expected, StudentT.CriticalValue(cl, df), relTol);

    // ---- Normal quantile vs SciPy norm.ppf ---------------------------------

    [Theory]
    [InlineData(0.5, 0.0)]
    [InlineData(0.975, 1.959963984540054)]
    [InlineData(0.025, -1.9599639845400545)]
    [InlineData(0.99, 2.3263478740408408)]
    [InlineData(0.995, 2.5758293035489004)]
    [InlineData(0.999, 3.090232306167813)]
    public void NormalQuantile_Matches_Scipy(double p, double expected)
    {
        var actual = StudentT.NormalQuantile(p);

        // Acklam's approximation: |error| < 1.15e-9.
        Assert.True(
            Math.Abs(actual - expected) <= 1.15e-8,
            $"NormalQuantile({p}) = {actual}, expected {expected} (SciPy).");
    }

    private static StatsSummary Compute(double[] values, double confidence)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        return StatsSummary.Compute(sorted, confidence);
    }
}
