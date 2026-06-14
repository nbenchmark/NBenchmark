using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class MultipleComparisonsTests
{
    [Fact]
    public void HolmBonferroni_SingleValue_ReturnsUnchanged()
    {
        var result = MultipleComparisons.HolmBonferroni([0.01]);
        Assert.Single(result);
        Assert.Equal(0.01, result[0]);
    }

    [Fact]
    public void HolmBonferroni_TwoValues_AppliesCorrection()
    {
        var result = MultipleComparisons.HolmBonferroni([0.01, 0.03]);

        // m=2: adjusted[0] = 2 * 0.01 = 0.02, adjusted[1] = max(1 * 0.03, 0.02) = 0.03
        Assert.Equal(0.02, result[0], 9);
        Assert.Equal(0.03, result[1], 9);
    }

    [Fact]
    public void HolmBonferroni_ThreeValues_Monotonicity()
    {
        var result = MultipleComparisons.HolmBonferroni([0.01, 0.02, 0.05]);

        // m=3: adj[0] = 3*0.01 = 0.03
        //      adj[1] = max(2*0.02=0.04, 0.03) = 0.04
        //      adj[2] = max(1*0.05=0.05, 0.04) = 0.05
        Assert.Equal(0.03, result[0], 9);
        Assert.Equal(0.04, result[1], 9);
        Assert.Equal(0.05, result[2], 9);
    }

    [Fact]
    public void HolmBonferroni_ClampsToOne()
    {
        var result = MultipleComparisons.HolmBonferroni([0.6, 0.7]);

        // m=2: adj[0] = 2*0.6 = 1.2 -> clamped to 1.0
        //      adj[1] = max(1*0.7=0.7, 1.0) = 1.0
        Assert.Equal(1.0, result[0]);
        Assert.Equal(1.0, result[1]);
    }

    [Fact]
    public void HolmBonferroni_IgnoresNaNInFamilySize_AndPreservesNaN()
    {
        var result = MultipleComparisons.HolmBonferroni([0.01, double.NaN, 0.03]);

        // Only testable hypotheses participate in the family size (m=2).
        // adj[0] = 2*0.01 = 0.02
        // adj[2] = max(1*0.03=0.03, 0.02) = 0.03
        // adj[1] = NaN
        Assert.Equal(0.02, result[0], 9);
        Assert.True(double.IsNaN(result[1]));
        Assert.Equal(0.03, result[2], 9);
    }

    [Fact]
    public void HolmBonferroni_EmptyInput_ReturnsEmpty()
    {
        var result = MultipleComparisons.HolmBonferroni([]);
        Assert.Empty(result);
    }

    [Fact]
    public void HolmBonferroni_AllNaN_ReturnsAllNaN()
    {
        var result = MultipleComparisons.HolmBonferroni([double.NaN, double.NaN]);
        Assert.Equal(2, result.Length);
        Assert.True(double.IsNaN(result[0]));
        Assert.True(double.IsNaN(result[1]));
    }

    [Fact]
    public void HolmBonferroni_AllZero_ReturnsAllZero()
    {
        var result = MultipleComparisons.HolmBonferroni([0.0, 0.0, 0.0]);
        Assert.All(result, v => Assert.Equal(0.0, v));
    }
}
