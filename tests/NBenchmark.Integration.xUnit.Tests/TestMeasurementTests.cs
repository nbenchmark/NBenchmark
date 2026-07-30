using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Integration.Abstractions;
using Xunit;

namespace NBenchmark.Integration.xUnit.Tests;

/// <summary>
///     The entry point all three test-framework integrations measure through.
///     <para>
///         These assert on <i>where</i> the measurement happened, not just that a number came back.
///         Every one of them would pass against a purely in-host implementation, which is exactly
///         the failure mode worth guarding: a silent fallback looks identical to success.
///     </para>
/// </summary>
public sealed class TestMeasurementTests
{
    private static RunSpec Fast() => new()
    {
        Options = MeasurementOptions.Default with
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
        },
    };

    private static MethodInfo Method<T>(string name)
        => typeof(T).GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)!;

    /// <summary>
    ///     The claim the whole stage rests on: an ordinary performance test is measured out of
    ///     process, under a runtime configuration the test host could never have applied to itself.
    /// </summary>
    [Fact]
    public async Task PlainTest_IsMeasuredInAWorker()
    {
        var measured = await TestMeasurement.MeasureAsync(
            Method<PlainSubject>(nameof(PlainSubject.Work)), new PlainSubject(), [], "Plain.Work", Fast());

        Assert.Null(measured.Refusal);
        Assert.Equal(IsolationStatus.Isolated, measured.Result.IsolationStatus);
        Assert.Equal("steady-state", measured.Result.RuntimeProfileName);

        // Without samples a gate cannot compute significance, which is most of what these
        // integrations are for.
        Assert.NotEmpty(measured.RawSamples);
    }

    /// <summary>
    ///     A class the framework injects into cannot be rebuilt in a worker, so it is measured in the
    ///     host - labelled, with the reason naming the injected dependency.
    /// </summary>
    [Fact]
    public async Task TestWithInjectedFixture_MeasuresInHost_AndSaysWhy()
    {
        var measured = await TestMeasurement.MeasureAsync(
            Method<FixtureSubject>(nameof(FixtureSubject.Work)),
            new FixtureSubject(new Fixture()),
            [],
            "Fixture.Work",
            Fast());

        Assert.Equal(IsolationStatus.InProcessLiveFixture, measured.Result.IsolationStatus);
        Assert.NotNull(measured.Refusal);
        Assert.Contains("Fixture", measured.Refusal);

        // Still measured - a fixture is a reason to label the number, not to refuse to produce one.
        Assert.False(measured.Result.Errored, measured.Result.ErrorMessage);
    }

    /// <summary>
    ///     An argument that is a live object keeps the measurement in the host and names the
    ///     parameter, rather than reconstructing something and measuring a different call.
    /// </summary>
    [Fact]
    public async Task TestWithObjectArgument_MeasuresInHost_AndNamesTheParameter()
    {
        var measured = await TestMeasurement.MeasureAsync(
            Method<PlainSubject>(nameof(PlainSubject.WorkWith)),
            new PlainSubject(),
            [new System.Text.StringBuilder("x")],
            "Plain.WorkWith",
            Fast());

        Assert.NotEqual(IsolationStatus.Isolated, measured.Result.IsolationStatus);
        Assert.NotNull(measured.Refusal);
        Assert.Contains("payload", measured.Refusal);
    }

    /// <summary>Simple arguments travel, so a parameterized test case still isolates.</summary>
    [Fact]
    public async Task TestWithSimpleArguments_IsMeasuredInAWorker()
    {
        var measured = await TestMeasurement.MeasureAsync(
            Method<PlainSubject>(nameof(PlainSubject.Spin)), new PlainSubject(), [500], "Plain.Spin(500)", Fast());

        Assert.Null(measured.Refusal);
        Assert.Equal(IsolationStatus.Isolated, measured.Result.IsolationStatus);
    }

    /// <summary>
    ///     An async test is awaited to completion in the worker. Measuring only the synchronous
    ///     prefix would report a fast, plausible number for work that never happened.
    /// </summary>
    [Fact]
    public async Task AsyncTest_IsAwaitedInTheWorker()
    {
        var spec = Fast();

        var measured = await TestMeasurement.MeasureAsync(
            Method<PlainSubject>(nameof(PlainSubject.DelayAsync)),
            new PlainSubject(),
            [],
            "Plain.DelayAsync",
            new RunSpec { Options = spec.Options with { Iterations = 3, WarmupIterations = 0 } });

        Assert.Equal(IsolationStatus.Isolated, measured.Result.IsolationStatus);

        Assert.True(
            measured.Result.Median > 10_000_000,
            $"a 20 ms body measured {measured.Result.Median / 1_000_000:F1} ms, so it was not awaited");
    }

    /// <summary>
    ///     A candidate and its reference, measured together with replicates, produce the paired ratio a
    ///     gate can be judged against - end to end, through real workers.
    /// </summary>
    /// <remarks>
    ///     The two bodies differ by 10x in spin count, so a large ratio is the expected answer and it
    ///     comes from the arguments rather than from either process. What the interval adds is the
    ///     statement the single-launch path cannot make: that three separate workers agreed.
    /// </remarks>
    [Fact]
    public async Task APairWithReplicates_ProducesAPairedRatio()
    {
        var spec = Fast();

        var pair = await TestMeasurement.MeasurePairAsync(
            new TestMeasurement.Target(
                Method<PlainSubject>(nameof(PlainSubject.Spin)), [5_000], "Plain.Spin(5000)"),
            new TestMeasurement.Target(
                Method<PlainSubject>(nameof(PlainSubject.Spin)), [500], "Plain.Spin(500)"),
            new PlainSubject(),
            new RunSpec { Options = spec.Options with { LaunchCount = 3 } });

        Assert.Null(pair.Candidate.Refusal);
        Assert.Equal(IsolationStatus.Isolated, pair.Candidate.Result.IsolationStatus);
        Assert.Equal(IsolationStatus.Isolated, pair.Reference!.Value.Result.IsolationStatus);

        var estimate = pair.PairedRatio;

        Assert.NotNull(estimate);
        Assert.Equal(3, estimate.Replicates);

        Assert.True(
            estimate.Value > 2,
            $"10x the spin work produced {estimate.Value:F2}x [{estimate.Lower:F2}-{estimate.Upper:F2}x]");
    }

    /// <summary>
    ///     One replicate cannot produce a paired ratio, and does not pretend to. This is the default, so
    ///     it is also the shape every existing suite keeps.
    /// </summary>
    [Fact]
    public async Task APairWithOneLaunch_ProducesNoPairedRatio()
    {
        var pair = await TestMeasurement.MeasurePairAsync(
            new TestMeasurement.Target(
                Method<PlainSubject>(nameof(PlainSubject.Spin)), [5_000], "Plain.Spin(5000)"),
            new TestMeasurement.Target(
                Method<PlainSubject>(nameof(PlainSubject.Spin)), [500], "Plain.Spin(500)"),
            new PlainSubject(),
            Fast());

        Assert.Null(pair.Candidate.Refusal);
        Assert.Null(pair.PairedRatio);
        Assert.Null(pair.Candidate.Result.LaunchStatistics);
    }

    /// <summary>
    ///     A pair that cannot be isolated goes to the host <b>together</b>, with the reason, and reports
    ///     no paired ratio - a sequence of measurements in the test host is not a set of replicates, and
    ///     treating it as one would put an interval on a number that has no reproducibility behind it.
    /// </summary>
    [Fact]
    public async Task AnUnisolatablePair_FallsBackToTheHostWithoutAPairedRatio()
    {
        var spec = Fast();

        var pair = await TestMeasurement.MeasurePairAsync(
            new TestMeasurement.Target(
                Method<FixtureSubject>(nameof(FixtureSubject.Work)), [], "Fixture.Work"),
            new TestMeasurement.Target(
                Method<FixtureSubject>(nameof(FixtureSubject.Other)), [], "Fixture.Other"),
            new FixtureSubject(new Fixture()),
            new RunSpec { Options = spec.Options with { LaunchCount = 3 } });

        Assert.NotNull(pair.Candidate.Refusal);
        Assert.Contains("Fixture", pair.Candidate.Refusal);
        Assert.Equal(IsolationStatus.InProcessLiveFixture, pair.Candidate.Result.IsolationStatus);
        Assert.Equal(IsolationStatus.InProcessLiveFixture, pair.Reference!.Value.Result.IsolationStatus);
        Assert.Null(pair.PairedRatio);
    }

    public class PlainSubject
    {
        public void Work() => Thread.SpinWait(300);

        public void Spin(int iterations) => Thread.SpinWait(iterations);

        public void WorkWith(System.Text.StringBuilder payload) => _ = payload.Length;

        public async ValueTask DelayAsync() => await Task.Delay(20);
    }

    public class FixtureSubject(Fixture fixture)
    {
        public void Work() => _ = fixture.Value;

        public void Other() => _ = fixture.Value + 1;
    }

    public sealed class Fixture
    {
        public int Value => 7;
    }
}
