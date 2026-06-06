using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class StudentTTests
{
    // Reference values from standard two-tailed t-tables (p = 0.975).
    [Theory]
    [InlineData(1, 12.706)]
    [InlineData(2, 4.303)]
    [InlineData(5, 2.571)]
    [InlineData(10, 2.228)]
    [InlineData(30, 2.042)]
    [InlineData(100, 1.984)]
    public void CriticalValue_95_Matches_Table(int df, double expected)
    {
        var t = StudentT.CriticalValue(0.95, df);
        Assert.Equal(expected, t, 0.01);
    }

    // Reference values from standard two-tailed t-tables (p = 0.995).
    [Theory]
    [InlineData(2, 9.925)]
    [InlineData(10, 3.169)]
    [InlineData(30, 2.750)]
    public void CriticalValue_99_Matches_Table(int df, double expected)
    {
        var t = StudentT.CriticalValue(0.99, df);
        Assert.Equal(expected, t, 0.01);
    }

    [Fact]
    public void CriticalValue_Approaches_Normal_For_Large_Df()
    {
        var t = StudentT.CriticalValue(0.95, 100_000);
        Assert.Equal(1.95996, t, 0.001);
    }

    [Fact]
    public void CriticalValue_Higher_Confidence_Is_Larger()
    {
        Assert.True(StudentT.CriticalValue(0.99, 20) > StudentT.CriticalValue(0.95, 20));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CriticalValue_Invalid_Df_Returns_NaN(int df)
    {
        Assert.True(double.IsNaN(StudentT.CriticalValue(0.95, df)));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void CriticalValue_Invalid_Confidence_Returns_NaN(double level)
    {
        Assert.True(double.IsNaN(StudentT.CriticalValue(level, 30)));
    }

    [Fact]
    public void NormalQuantile_Matches_Known_Values()
    {
        Assert.Equal(0.0, StudentT.NormalQuantile(0.5), 6);
        Assert.Equal(1.95996, StudentT.NormalQuantile(0.975), 4);
        Assert.Equal(-1.95996, StudentT.NormalQuantile(0.025), 4);
        Assert.Equal(2.32635, StudentT.NormalQuantile(0.99), 3);
    }
}