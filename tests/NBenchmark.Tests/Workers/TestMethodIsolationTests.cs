using System.Reflection;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Measuring a test-framework method in a worker: a method with no <c>[Benchmark]</c> attribute,
///     whose declaring type the worker builds for itself.
/// </summary>
[Collection(nameof(RealWorkerCollection))]
public sealed class TestMethodIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public TestMethodIsolationTests()
        => WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static MeasurementOptions Fast() => MeasurementOptions.Default with
    {
        Iterations = 16,
        WarmupIterations = 1,
        OpsPerSample = 1,
        AutoTune = AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(5),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        },
    };

    private static MethodInfo Method(string name)
        => typeof(SubjectTests).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)!;

    /// <summary>
    ///     The core claim: an ordinary test method is measured in a worker, under the worker's
    ///     runtime profile rather than the test host's.
    /// </summary>
    [Fact]
    public async Task PlainTestMethod_IsMeasuredInAWorker()
    {
        var outcome = await TestMethodRunner.RunAsync(
            Method(nameof(SubjectTests.Fast)), [], "SubjectTests.Fast", Fast(), LaunchCounts.Single);

        Assert.True(outcome.Measured, outcome.Refusal);
        Assert.False(outcome.Result!.Errored, outcome.Result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, outcome.Result.IsolationStatus);

        // The stamp is read from the measuring process's own environment, so this proves the
        // measurement happened somewhere the profile could actually be applied.
        Assert.Equal("steady-state", outcome.Result.RuntimeProfileName);

        // Raw samples must survive: without them a gate cannot compute significance, which is the
        // exact defect that shipped when isolated results arrived sample-less.
        Assert.NotEmpty(outcome.RawSamples);
    }

    /// <summary>
    ///     A static test method has no receiver. The expression-tree path used by discovery never
    ///     met one, because a <c>[Benchmark]</c> is always an instance method.
    /// </summary>
    [Fact]
    public async Task StaticTestMethod_IsMeasuredInAWorker()
    {
        var outcome = await TestMethodRunner.RunAsync(
            Method(nameof(SubjectTests.FastStatic)), [], "SubjectTests.FastStatic", Fast(), LaunchCounts.Single);

        Assert.True(outcome.Measured, outcome.Refusal);
        Assert.False(outcome.Result!.Errored, outcome.Result.ErrorMessage);
    }

    /// <summary>
    ///     Arguments must arrive as the values the test declared. Measuring the wrong argument would
    ///     produce a plausible number for a call the test never made.
    /// </summary>
    [Fact]
    public async Task ParameterizedTestMethod_MeasuresTheDeclaredArguments()
    {
        var cheap = await TestMethodRunner.RunAsync(
            Method(nameof(SubjectTests.Spin)), [200], "Spin(200)", Fast(), LaunchCounts.Single);

        var costly = await TestMethodRunner.RunAsync(
            Method(nameof(SubjectTests.Spin)), [20_000], "Spin(20000)", Fast(), LaunchCounts.Single);

        Assert.True(cheap.Measured, cheap.Refusal);
        Assert.True(costly.Measured, costly.Refusal);

        Assert.True(
            costly.Result!.Median > cheap.Result!.Median * 5,
            $"the argument did not reach the worker: 200 spins={cheap.Result.Median:F0}ns, "
            + $"20000 spins={costly.Result.Median:F0}ns");
    }

    /// <summary>
    ///     A <c>long</c> parameter given an <c>int</c> literal is the ordinary C# case, and the
    ///     encoding has to follow the <i>declared</i> type rather than the boxed value's type.
    /// </summary>
    [Fact]
    public async Task WideningArgument_BindsToTheDeclaredParameterType()
    {
        var outcome = await TestMethodRunner.RunAsync(
            Method(nameof(SubjectTests.TakesLong)), [1], "TakesLong(1)", Fast(), LaunchCounts.Single);

        Assert.True(outcome.Measured, outcome.Refusal);
        Assert.False(outcome.Result!.Errored, outcome.Result.ErrorMessage);
    }

    /// <summary>An enum argument round-trips by name, not by its numeric value.</summary>
    [Fact]
    public async Task EnumArgument_RoundTrips()
    {
        var outcome = await TestMethodRunner.RunAsync(
            Method(nameof(SubjectTests.TakesEnum)), [Choice.Second], "TakesEnum(Second)", Fast(), LaunchCounts.Single);

        Assert.True(outcome.Measured, outcome.Refusal);
        Assert.False(outcome.Result!.Errored, outcome.Result.ErrorMessage);
    }

    /// <summary>
    ///     An argument that is a live object is refused before a worker is launched, and the message
    ///     names the parameter rather than reporting a generic transport failure.
    /// </summary>
    [Fact]
    public void ObjectArgument_IsRefusedWithTheParameterNamed()
    {
        var addressable = TestMethodRunner.CanAddress(Method(nameof(SubjectTests.TakesObject)), out var refusal);

        Assert.False(addressable);
        Assert.Contains("payload", refusal);
        Assert.Contains("StringBuilder", refusal);
    }

    /// <summary>An async test method must be awaited to completion, not merely started.</summary>
    [Fact]
    public async Task AsyncTestMethod_IsAwaitedInTheWorker()
    {
        var outcome = await TestMethodRunner.RunAsync(
            Method(nameof(SubjectTests.DelayAsync)), [], "SubjectTests.DelayAsync",
            Fast() with
            {
                Iterations = 3,
                WarmupIterations = 0,
            },
            LaunchCounts.Single);

        Assert.True(outcome.Measured, outcome.Refusal);
        Assert.False(outcome.Result!.Errored, outcome.Result.ErrorMessage);

        // The body delays 20 ms. A reading far below that means the await was skipped and only the
        // synchronous prefix was timed.
        Assert.True(
            outcome.Result.Median > 10_000_000,
            $"a 20 ms body measured {outcome.Result.Median / 1_000_000:F1} ms, so it was not awaited");
    }

    /// <summary>
    ///     The end-to-end paired path: two methods, three real workers, one paired ratio.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two subjects are the <i>same method</i> with a 10x difference in argument, so the true
    ///         ratio is known to be large and to come from the arguments rather than from the bodies.
    ///         That also exercises two payload entries sharing one metadata token, which is what a
    ///         parameterized test compared against itself produces.
    ///     </para>
    ///     <para>
    ///         What this pins that a fake launcher cannot: that a real worker accepts a group of several
    ///         test methods at all, reports them under the names the caller asked for, and that three
    ///         such workers produce launch statistics the paired estimator can pair.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task APairWithReplicates_IsMeasuredCoResidentAndPaired()
    {
        var outcome = await TestMethodRunner.RunAsync(
            [
                new TestMethodRunner.Subject(Method(nameof(SubjectTests.Spin)), [20_000], "Spin(20000)"),
                new TestMethodRunner.Subject(Method(nameof(SubjectTests.Spin)), [2_000], "Spin(2000)"),
            ],
            Fast(),
            launchCount: 3);

        Assert.True(outcome.Measured, outcome.Refusal);
        Assert.Equal(2, outcome.Measurements.Count);

        // In request order, under the requested names. Discovery's own '<Class>.<Name>' convention would
        // report these as 'SubjectTests.Spin(20000)', which is not the name the caller is gating on.
        Assert.Equal("Spin(20000)", outcome.Measurements[0].Result.Name);
        Assert.Equal("Spin(2000)", outcome.Measurements[1].Result.Name);

        var costly = outcome.Measurements[0].Result;
        var cheap = outcome.Measurements[1].Result;

        Assert.False(costly.Errored, costly.ErrorMessage);
        Assert.False(cheap.Errored, cheap.ErrorMessage);
        Assert.Equal(3, costly.LaunchStatistics!.LaunchCount);
        Assert.Equal(3, cheap.LaunchStatistics!.LaunchCount);

        // Pooled across the three launches, which is what a significance test on this path reads.
        Assert.NotEmpty(outcome.Measurements[0].RawSamples);

        var estimate = NBenchmark.Stats.LogRatio.Estimate(costly, cheap);

        Assert.NotNull(estimate);
        Assert.Equal(3, estimate.Replicates);

        Assert.True(
            estimate.Value > 2,
            $"10x the spin work produced a paired ratio of {estimate.Value:F2}x "
            + $"[{estimate.Lower:F2}-{estimate.Upper:F2}x]");
    }

    /// <summary>The subject under measurement - deliberately not a benchmark class.</summary>
    public class SubjectTests
    {
        public void Fast() => Thread.SpinWait(200);

        public static void FastStatic() => Thread.SpinWait(200);

        public void Spin(int iterations) => Thread.SpinWait(iterations);

        public void TakesLong(long value) => Thread.SpinWait((int)value + 200);

        public void TakesEnum(Choice choice) => Thread.SpinWait((int)choice + 200);

        public void TakesObject(System.Text.StringBuilder payload) => _ = payload.Length;

        public async ValueTask DelayAsync() => await Task.Delay(20);
    }

    public enum Choice
    {
        First = 0,
        Second = 1,
    }
}
