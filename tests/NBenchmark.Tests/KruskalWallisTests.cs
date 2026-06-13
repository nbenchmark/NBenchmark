using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class KruskalWallisTests
{
    [Fact]
    public void ThreeSeparatedGroups_ProduceKnownStatistic()
    {
        // Groups {1,2,3}, {4,5,6}, {7,8,9} have rank sums 6, 15, 24.
        // H = 12/(9·10)·(6²+15²+24²)/3 − 3·10 = 7.2 ; p = e^(−3.6) ≈ 0.0273.
        var groups = new[]
        {
            new double[] { 1, 2, 3 },
            new double[] { 4, 5, 6 },
            new double[] { 7, 8, 9 },
        };

        var result = KruskalWallis.Test(groups);

        Assert.True(result.IsValid);
        Assert.Equal(2, result.DegreesOfFreedom);
        Assert.Equal(3, result.GroupCount);
        Numerics.AssertRelativeClose(7.2, result.H, 1e-9);
        Numerics.AssertRelativeClose(Math.Exp(-3.6), result.PValue, 1e-6);
    }

    [Fact]
    public void TieCorrection_MatchesHandComputedValue()
    {
        // {1,2} vs {2,3}: the shared 2's get mid-rank 2.5.
        // Uncorrected H = 1.35; tie factor C = 1 − 6/60 = 0.9; corrected H = 1.5.
        var groups = new[]
        {
            new double[] { 1, 2 },
            new double[] { 2, 3 },
        };

        var result = KruskalWallis.Test(groups);

        Assert.True(result.IsValid);
        Assert.Equal(1, result.DegreesOfFreedom);
        Numerics.AssertRelativeClose(1.5, result.H, 1e-9);
    }

    [Fact]
    public void AllIdenticalValues_AreNotSignificant()
    {
        var groups = new[]
        {
            new double[] { 5, 5, 5 },
            new double[] { 5, 5, 5 },
            new double[] { 5, 5, 5 },
        };

        var result = KruskalWallis.Test(groups);

        Assert.True(result.IsValid);
        Assert.Equal(0.0, result.H, 10);
        Assert.Equal(1.0, result.PValue, 10);
    }

    [Fact]
    public void ClearlySeparatedGroups_YieldSmallPValue()
    {
        var groups = new[]
        {
            new double[] { 10, 11, 12, 13, 14 },
            new double[] { 50, 51, 52, 53, 54 },
            new double[] { 90, 91, 92, 93, 94 },
        };

        var result = KruskalWallis.Test(groups);

        Assert.True(result.PValue < 0.01);
    }

    [Fact]
    public void OverlappingGroups_YieldLargePValue()
    {
        var rng = new Random(7);
        double[] Sample() => Enumerable.Range(0, 30).Select(_ => (double)rng.Next(90, 110)).ToArray();

        var result = KruskalWallis.Test([Sample(), Sample(), Sample()]);

        Assert.True(result.PValue > 0.05);
    }

    [Fact]
    public void FewerThanTwoGroups_IsNotValid()
    {
        var result = KruskalWallis.Test([new double[] { 1, 2, 3 }]);

        Assert.False(result.IsValid);
        Assert.True(double.IsNaN(result.PValue));
    }

    [Fact]
    public void EmptyGroup_IsNotValid()
    {
        var result = KruskalWallis.Test([new double[] { 1, 2, 3 }, []]);

        Assert.False(result.IsValid);
    }
}
