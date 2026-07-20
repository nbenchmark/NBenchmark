using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     Cross-checks the erfc-based standard-normal CDF used by the Mann-Whitney asymptotic
///     branch against SciPy 1.18.0 (<c>scipy.stats.norm.cdf</c>). The point is the deep tail:
///     the previous Abramowitz-Stegun 7.1.26 form had ~1.5e-7 absolute error, which made
///     exported p-values below ~1e-7 meaningless. These pin accuracy down past 1e-12.
/// </summary>
public class NormalCdfCrossCheckTests
{
    [Theory]
    [InlineData(0.0, 5.0000000000000000e-01)]
    [InlineData(0.5, 6.9146246127401312e-01)]
    [InlineData(1.0, 8.4134474606854293e-01)]
    [InlineData(1.96, 9.7500210485177952e-01)]
    [InlineData(-1.0, 1.5865525393145707e-01)]
    [InlineData(3.0, 9.9865010196836990e-01)]
    [InlineData(-3.0, 1.3498980316300931e-03)]
    public void NormalCdf_Matches_SciPy_MidRange(double z, double expected)
    {
        Numerics.AssertRelativeClose(expected, MannWhitneyU.NormalCdf(z), 1e-12);
    }

    // Deep left tail - where the old approximation was pure noise.
    [Theory]
    [InlineData(-6.0, 9.8658764503769458e-10)]
    [InlineData(-7.0, 1.2798125438858350e-12)]
    [InlineData(-8.0, 6.2209605742717405e-16)]
    public void NormalCdf_DeepTail_Matches_SciPy(double z, double expected)
    {
        Numerics.AssertRelativeClose(expected, MannWhitneyU.NormalCdf(z), 1e-9);
    }

    [Fact]
    public void NormalCdf_Symmetry_Holds()
    {
        foreach (var z in new[] { 0.3, 1.1, 2.7, 4.2, 6.5 })
        {
            Numerics.AssertRelativeClose(1.0, MannWhitneyU.NormalCdf(z) + MannWhitneyU.NormalCdf(-z), 1e-12);
        }
    }
}
