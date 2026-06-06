using NBenchmark;
using NBenchmark.Stats;
using NBenchmark.Discovery;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests;

public class BenchmarkSuiteTests
{
    [Fact]
    public void Add_Rejects_Duplicate_Names()
    {
        var suite = new BenchmarkSuite("dup");
        suite.Add("foo", () => { });

        Assert.Throws<ArgumentException>(() => suite.Add("foo", () => { }));
    }

    [Fact]
    public async Task RunAsync_Executes_All_Added_Benchmarks()
    {
        var results = await new BenchmarkSuite("capture")
            .Add("a", () => { })
            .Add("b", () => { })
            .WithWarmup(1)
            .WithIterations(2)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RunAsync_WithBaseline_Significance_Sets_IsSignificant()
    {
        var results = await new BenchmarkSuite("sig")
            .Add("baseline", () => Thread.SpinWait(1000))
            .Add("faster",   () => Thread.SpinWait(500))
            .WithBaseline("baseline")
            .WithWarmup(2)
            .WithIterations(20)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var faster = results.Single(r => r.Name == "faster");
        Assert.True(faster.IsSignificant.HasValue);
        Assert.NotNull(faster.PValue);
    }

    [Fact]
    public async Task WithBaseline_Not_In_Suite_Throws()
    {
        var suite = new BenchmarkSuite("bad")
            .Add("a", () => { })
            .WithBaseline("missing");

        await Assert.ThrowsAsync<InvalidOperationException>(() => suite.RunAsync());
    }

    [Fact]
    public async Task RunAsync_Captures_Exception_As_Errored_Result()
    {
        var results = await new BenchmarkSuite("boom")
            .Add("explodes", () => throw new InvalidOperationException("nope"))
            .Add("calm",     () => { })
            .WithWarmup(1)
            .WithIterations(5)
            .WithOutlierMode(OutlierMode.None)
            .RunAsync();

        var errored = results.Single(r => r.Name == "explodes");
        Assert.True(errored.Errored);
        Assert.Contains("nope", errored.ErrorMessage);

        var calm = results.Single(r => r.Name == "calm");
        Assert.False(calm.Errored);
    }
}
