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

        var result = MannWhitneyU.Test(a, b);

        Assert.Equal(1.0, result.PValue, 3);
    }

    [Fact]
    public void Clearly_Different_Samples_Return_Small_PValue()
    {
        var a = new double[] { 10, 12, 11, 13, 10 };
        var b = new double[] { 100, 102, 101, 103, 100 };

        var result = MannWhitneyU.Test(a, b);

        Assert.True(result.PValue < 0.05);
    }

    [Fact]
    public void Slightly_Different_Samples_Return_Large_PValue()
    {
        var rng = new Random(42);
        var a = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();
        var b = Enumerable.Range(0, 50).Select(_ => (double)rng.Next(90, 110)).ToArray();

        var result = MannWhitneyU.Test(a, b);

        Assert.True(result.PValue > 0.05);
    }

    [Fact]
    public void Single_Element_Samples_Return_NaN()
    {
        var a = new double[] { 10 };
        var b = new double[] { 20 };

        var result = MannWhitneyU.Test(a, b);

        Assert.True(double.IsNaN(result.PValue));
    }

    [Fact]
    public void Empty_Sample_Returns_NaN()
    {
        var result = MannWhitneyU.Test(Array.Empty<double>(), new double[] { 1, 2, 3 });
        Assert.True(double.IsNaN(result.PValue));
    }

    [Fact]
    public void Small_But_Valid_Samples_Use_Exact_Path()
    {
        // 4 vs 16 observations: both groups have >= 2 values, so the test is
        // defined. Combined n = 20 and tie-free, so the exact path is taken.
        // Group A is strictly below group B, so the result is highly significant.
        var a = new double[] { 1, 2, 3, 4 };
        var b = new double[] { 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 };

        var result = MannWhitneyU.Test(a, b);

        Assert.False(double.IsNaN(result.PValue));
        Assert.True(result.PValue < 0.05);
    }

    [Fact]
    public void Tied_Values_Handled_Correctly()
    {
        var a = new double[] { 10, 10, 10, 20, 20 };
        var b = new double[] { 30, 30, 30, 40, 40 };

        var result = MannWhitneyU.Test(a, b);

        Assert.True(result.PValue < 0.05);
    }

    [Fact]
    public void Result_Struct_Populates_PValue_And_CliffsDelta()
    {
        // End-to-end check that the MannWhitneyUResult struct carries both
        // the p-value and Cliff's delta and that CliffsDelta is not NaN.
        var a = new double[] { 10, 20, 30, 40, 50 };
        var b = new double[] { 100, 200, 300, 400, 500 };

        var result = MannWhitneyU.Test(a, b);

        Assert.False(double.IsNaN(result.PValue));
        Assert.False(double.IsNaN(result.CliffsDelta));
    }

    [Fact]
    public void CliffsDelta_Identical_Samples_Is_Zero()
    {
        // When every value matches, the rank sums split evenly and the
        // distributions overlap completely: CliffsDelta = 0.
        var a = new double[] { 10, 20, 30, 40, 50 };
        var b = new double[] { 10, 20, 30, 40, 50 };

        var result = MannWhitneyU.Test(a, b);

        Assert.Equal(0.0, result.CliffsDelta, 10);
    }

    [Fact]
    public void CliffsDelta_Completely_Separated_Samples_Below_Above_Is_PlusOne()
    {
        // All baseline samples strictly below all candidate samples. Under the
        // convention "positive = candidate larger (slower)", CliffsDelta must be +1.
        var baseline = new double[] { 1, 2, 3, 4 };
        var candidate = new double[] { 5, 6, 7, 8 };

        var result = MannWhitneyU.Test(baseline, candidate);

        Assert.Equal(1.0, result.CliffsDelta, 10);
    }

    [Fact]
    public void CliffsDelta_Completely_Separated_Samples_Above_Below_Is_MinusOne()
    {
        // Mirror of the above: all baseline samples strictly above all candidate
        // samples, so candidate is uniformly smaller (faster). CliffsDelta = -1.
        var baseline = new double[] { 5, 6, 7, 8 };
        var candidate = new double[] { 1, 2, 3, 4 };

        var result = MannWhitneyU.Test(baseline, candidate);

        Assert.Equal(-1.0, result.CliffsDelta, 10);
    }

    [Fact]
    public void CliffsDelta_Slightly_Shifted_Candidate_Is_Small_Positive()
    {
        // Candidate samples are shifted slightly above baseline samples. A
        // closed-form sanity check: U1 counts (a, b) pairs where a > b = 6,
        // U2 counts (a, b) pairs where a < b = 10, n1 * n2 = 16, so
        // CliffsDelta = (U2 - U1) / (n1 * n2) = (10 - 6) / 16 = 0.25.
        var baseline = new double[] { 1, 3, 5, 7 };
        var candidate = new double[] { 2, 4, 6, 9 };

        var result = MannWhitneyU.Test(baseline, candidate);

        Assert.Equal(0.25, result.CliffsDelta, 10);
    }
}
