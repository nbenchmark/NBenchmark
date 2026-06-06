using NBenchmark.Stats;
using Xunit;

namespace NBenchmark.Tests;

public class MannWhitneyUTests
{
    [Fact]
    public void Identical_Samples_Return_PValue_One()
    {
        var a = new double[] { 10, 20, 30, 40, 50 };
        var b = new double[] { 10, 20, 30, 40, 50 };

        var p = MannWhitneyU.Test(a, b);

        Assert.Equal(1.0, p, 3);
    }

    [Fact]
    public void Clearly_Different_Samples_Return_Small_PValue()
    {
        var a = new double[] { 10, 12, 11, 13, 10 };
        var b = new double[] { 100, 102, 101, 103, 100 };

        var p = MannWhitneyU.Test(a, b);

        Assert.True(p < 0.05);
    }

    [Fact]
    public void Slightly_Different_Samples_Return_Large_PValue()
    {
        var rng = new Random(42);
        var a = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var b = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();

        var p = MannWhitneyU.Test(a, b);

        Assert.True(p > 0.05);
    }

    [Fact]
    public void Single_Element_Samples_Return_NaN()
    {
        var a = new double[] { 10 };
        var b = new double[] { 20 };

        var p = MannWhitneyU.Test(a, b);

        Assert.True(double.IsNaN(p));
    }

    [Fact]
    public void Empty_Sample_Returns_NaN()
    {
        var p = MannWhitneyU.Test(Array.Empty<double>(), new double[] { 1, 2, 3 });
        Assert.True(double.IsNaN(p));
    }

    [Fact]
    public void Small_Sample_Returns_NaN()
    {
        var a = new double[] { 1, 2, 3, 4 };
        var b = new double[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        var p = MannWhitneyU.Test(a, b);

        Assert.True(double.IsNaN(p));
    }

    [Fact]
    public void Tied_Values_Handled_Correctly()
    {
        var a = new double[] { 10, 10, 10, 20, 20 };
        var b = new double[] { 30, 30, 30, 40, 40 };

        var p = MannWhitneyU.Test(a, b);

        Assert.True(p < 0.05);
    }
}