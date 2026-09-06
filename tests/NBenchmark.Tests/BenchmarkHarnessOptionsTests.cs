using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     The harness can express in code what the CLI can express in flags.
/// </summary>
/// <remarks>
///     The measurement and statistics setters used to exist on <see cref="BenchmarkSuite" /> only, so
///     <c>--samples</c>, <c>--warmup-samples</c>, <c>--seed</c> and the rest worked in harness mode
///     while the corresponding <c>With*</c> methods did not exist - a user who wanted those values
///     fixed in code rather than on the command line had nowhere to write them.
/// </remarks>
public class BenchmarkHarnessOptionsTests
{
    [Fact]
    public async Task WithSamples_Pins_The_Measured_Sample_Count()
    {
        var results = await RunAsync(harness => harness
            .WithSamples(5)
            .WithWarmupSamples(1)
            .WithOutlierMode(OutlierMode.None));

        Assert.All(results, r => Assert.Equal(5, r.SampleCount));
    }

    [Fact]
    public async Task Configure_Merges_Into_The_Options_Already_Set()
    {
        var results = await RunAsync(harness => harness
            .WithWarmupSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .Configure(o => o with { Samples = 6 }));

        Assert.All(results, r => Assert.Equal(6, r.SampleCount));
    }

    [Fact]
    public void Configure_Rejects_A_Null_Return()
    {
        var harness = BenchmarkHarness.Create([]);

        Assert.Throws<BenchmarkConfigurationException>(() => harness.Configure(_ => null!));
    }

    [Fact]
    public async Task Cli_Sample_Count_Wins_Over_The_Fluent_One()
    {
        var results = await RunAsync(
            harness => harness.WithSamples(5).WithWarmupSamples(1).WithOutlierMode(OutlierMode.None),
            "--samples", "4");

        Assert.All(results, r => Assert.Equal(4, r.SampleCount));
    }

    [Fact]
    public async Task WithSignificance_Disabled_Leaves_Every_Result_Untested()
    {
        var results = await RunAsync(harness => harness
            .WithSamples(5)
            .WithWarmupSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithSignificance(false));

        Assert.All(results, r => Assert.Equal(SignificanceVerdict.NotTested, r.SignificanceVerdict));
    }

    [Fact]
    public async Task WithConfidenceLevel_Reaches_The_Result()
    {
        var results = await RunAsync(harness => harness
            .WithSamples(5)
            .WithWarmupSamples(1)
            .WithOutlierMode(OutlierMode.None)
            .WithConfidenceLevel(0.99));

        Assert.All(results, r => Assert.Equal(0.99, r.ConfidenceLevel));
    }

    private static async Task<IReadOnlyList<BenchmarkResult>> RunAsync(
        Func<BenchmarkHarness, BenchmarkHarness> configure,
        params string[] extraArgs)
    {
        string[] args = ["--filter", "TestBenchmarks.*", "--launch-count", "1", .. extraArgs];

        return await configure(
                BenchmarkHarness.Create(args)
                    .AddFromAssembly<TestBenchmarks>()
                    .WithRunOrder(RunOrder.Declaration)
                    .WithIsolation(Isolation.Off))
            .RunAsync();
    }
}
