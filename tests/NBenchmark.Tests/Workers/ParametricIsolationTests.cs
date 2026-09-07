using NBenchmark;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     D7: a <c>[Arguments]</c> value survives an isolated run with the same fidelity it has
///     in-process - a <c>Type</c> argument no longer crashes the frame write, and every other value
///     renders and groups exactly as its in-process counterpart would.
/// </summary>
/// <remarks>
///     <para>
///         <c>BenchmarkParameter.Value</c> is declared <c>object?</c>, which - before this - crossed
///         the worker/coordinator wire as a raw <c>System.Text.Json</c> value: a
///         <c>[Arguments(typeof(X))]</c> argument is a <c>System.RuntimeType</c>, which the
///         serializer refuses outright, and every other value came back as a type-blind
///         <c>JsonElement</c> - an enum rendered as its underlying number, and the grouping key's
///         type component read <c>System.Text.Json.JsonElement</c> for every isolated parameter
///         regardless of what it actually held.
///     </para>
///     <para>
///         Run through a real worker rather than only through <c>FrameChannel</c> directly, because
///         the failure this pins is specifically what a completed frame looks like on arrival - the
///         thing a fixture-level round-trip test would not by itself prove reaches
///         <c>BenchmarkResult.ParameterSet</c> unchanged.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class ParametricIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public ParametricIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SingleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static BenchmarkHarness Harness()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        return harness
            .AddFromAssembly(typeof(ParametricIsolationTests).Assembly)
            .WithLaunchCount(1)
            .WithOptions(MeasurementOptions.Default with
            {
                Samples = 8,
                WarmupSamples = 1,
                AutoTune = AutoTuneOptions.Default with
                {
                    MaxTuningTime = TimeSpan.FromSeconds(5),
                    MinWarmupTime = TimeSpan.Zero,
                    MinMeasurementTime = TimeSpan.Zero,
                    RequireJitQuiescence = false,
                    EnableJitterCalibration = false,
                },
            });
    }

    /// <summary>
    ///     The crash this closes: a <c>Type</c> argument used to fail the frame write, and the row
    ///     came back as a synthesized "did not return a result" error rather than a measurement.
    /// </summary>
    [Fact]
    public async Task A_Type_Valued_Case_Isolates_Instead_Of_Losing_The_Row()
    {
        var results = await Harness()
            .FilterCategories(["typed-case"])
            .RunAsync();

        var intCase = Assert.Single(results, r => r.Name.Contains("(kind=System.Int32)"));

        Assert.False(intCase.Errored, intCase.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, intCase.IsolationStatus);
        Assert.NotEmpty(intCase.RawSamples);

        var stringCase = Assert.Single(results, r => r.Name.Contains("(kind=System.String)"));

        Assert.False(stringCase.Errored, stringCase.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, stringCase.IsolationStatus);
    }

    /// <summary>
    ///     An enum case still renders by member name, not by its underlying number, once isolated -
    ///     matching what the exact same value already shows in-process.
    /// </summary>
    [Fact]
    public async Task An_Enum_Valued_Case_Isolates_And_Renders_By_Name()
    {
        var results = await Harness()
            .FilterCategories(["enum-case"])
            .RunAsync();

        var slow = Assert.Single(results, r => r.Name.Contains("Slow"));

        Assert.False(slow.Errored, slow.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, slow.IsolationStatus);
        Assert.Contains("(mode=Slow)", slow.Name);

        var parameter = Assert.Single(slow.ParameterSet);
        Assert.Equal("Slow", BenchmarkParameter.FormatValue(parameter.Value));
    }

    /// <summary>
    ///     The grouping key an isolated enum parameter produces now agrees with the key the same
    ///     value produces in-process - both the display text and the type name, which used to read
    ///     <c>System.Text.Json.JsonElement</c> for every isolated parameter regardless of what it
    ///     actually held.
    /// </summary>
    [Fact]
    public async Task An_Isolated_Parameter_Key_Matches_Its_InProcess_Shape()
    {
        var results = await Harness()
            .FilterCategories(["enum-case"])
            .RunAsync();

        var slow = Assert.Single(results, r => r.Name.Contains("Slow"));

        var isolatedKey = BenchmarkParameter.GetKey(slow.ParameterSet);
        var inProcessKey = BenchmarkParameter.GetKey([new BenchmarkParameter("mode", ProcessingMode.Slow)]);

        Assert.Equal(inProcessKey, isolatedKey);
    }
}

public enum ProcessingMode
{
    Fast,
    Slow,
}

public sealed class TypedCaseIsolationBenchmarks
{
    [Benchmark]
    [BenchmarkCategory("typed-case")]
    [Arguments(typeof(int))]
    [Arguments(typeof(string))]
    public int Run(Type kind) => kind == typeof(int) ? 1 : 2;

    [Benchmark]
    [BenchmarkCategory("enum-case")]
    [Arguments(ProcessingMode.Fast)]
    [Arguments(ProcessingMode.Slow)]
    public int RunMode(ProcessingMode mode) => (int)mode;
}
