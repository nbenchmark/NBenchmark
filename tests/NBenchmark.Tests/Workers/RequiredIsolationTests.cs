using NBenchmark.Attributes;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Lifecycle;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Phase 2: a refusal is an error, and in-process measurement is something you ask for.
/// </summary>
/// <remarks>
///     The gate keys on <see cref="IsolationStatusExtensions.IsRefusal" />, never on
///     <c>!IsIsolated()</c>. That distinction is the whole of what makes the default acceptable: every
///     deliberate route to the host process - <c>--dry-run</c>, <c>--in-process</c>,
///     <c>[InProcess]</c>, <c>RunInProcess</c>, <c>WithIsolation(false)</c>, <c>AddInProcess</c> -
///     produces <see cref="IsolationStatus.InProcessRequested" />, and every one of them has to stay
///     legal.
/// </remarks>
public sealed class RequiredIsolationTests
{
    private static MeasurementOptions Required => MeasurementOptions.Default with { RequireIsolation = true };

    /// <summary>
    ///     Enough measurement to produce a row and no more. These tests are about the isolation
    ///     decision, which is made before a body is invoked.
    /// </summary>
    private static MeasurementOptions Fast => MeasurementOptions.Default with
    {
        Iterations = 2,
        WarmupIterations = 0,
        OpsPerSample = 1,
        AutoTune = AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(2),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        },
    };

    /// <summary>
    ///     The default. Recorded as a test because it is the one line of Phase 2 that changes what
    ///     happens to a user who configured nothing.
    /// </summary>
    [Fact]
    public void RequireIsolation_IsOnByDefault()
        => Assert.True(MeasurementOptions.Default.RequireIsolation);

    /// <summary>
    ///     A deliberate in-process run passes the gate. Keyed on <c>!IsIsolated()</c> this would throw
    ///     for <c>--dry-run</c>, which is the run that has no measurement to isolate in the first place.
    /// </summary>
    [Fact]
    public void ThrowIfRequired_InProcessRequested_DoesNotThrow()
        => IsolationAudit.ThrowIfRequired(Required, "deliberate", IsolationStatus.InProcessRequested, null);

    /// <summary>An isolated run has nothing to gate.</summary>
    [Fact]
    public void ThrowIfRequired_Isolated_DoesNotThrow()
        => IsolationAudit.ThrowIfRequired(Required, "clean", IsolationStatus.Isolated, null);

    /// <summary>
    ///     A refusal throws, and the message carries the four things a reader needs: which benchmark,
    ///     why, the remedy, and how to ask for the host process deliberately.
    /// </summary>
    /// <remarks>
    ///     The opt-out is asserted because without it this is a dead end. The gate is now on by default,
    ///     so the first encounter with it is a run that used to produce numbers and now throws - a
    ///     message that only says "no" leaves the reader with nothing to do next.
    /// </remarks>
    [Fact]
    public void ThrowIfRequired_Refusal_NamesTheBenchmark_TheReason_TheRemedyAndTheOptOut()
    {
        var error = Assert.Throws<InvalidOperationException>(() => IsolationAudit.ThrowIfRequired(
            Required, "sorter", IsolationStatus.InProcessCapturedState, "it captures 'comparer'."));

        Assert.Contains("sorter", error.Message, StringComparison.Ordinal);
        Assert.Contains("captures 'comparer'", error.Message, StringComparison.Ordinal);
        Assert.Contains(IsolationStatus.InProcessCapturedState.ToRemedy()!, error.Message, StringComparison.Ordinal);
        Assert.Contains("RunInProcess", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Off, and a refusal is a labelled fallback again.</summary>
    [Fact]
    public void ThrowIfRequired_WhenNotRequired_DoesNotThrow()
        => IsolationAudit.ThrowIfRequired(
            MeasurementOptions.Default with { RequireIsolation = false },
            "sorter",
            IsolationStatus.InProcessCapturedState,
            "it captures 'comparer'.");

    /// <summary>
    ///     Several refusals are reported together rather than one per run.
    /// </summary>
    /// <remarks>
    ///     The list overload exists for this: isolatability is decided for every class before the first
    ///     benchmark runs, so a run with three un-isolatable classes can say so once. Throwing on the
    ///     first would surface them one per attempt, which is the slowest possible way to learn three
    ///     facts that were all known before anything was measured.
    /// </remarks>
    [Fact]
    public void ThrowIfRequired_ManyRefusals_NamesEveryOne()
    {
        var error = Assert.Throws<InvalidOperationException>(() => IsolationAudit.ThrowIfRequired(
            Required,
            [
                new IsolationRefusal("First", IsolationStatus.InProcessCapturedState, "it captures 'a'."),
                new IsolationRefusal("Second", IsolationStatus.InProcessLiveFixture, "its instances come from here."),
                new IsolationRefusal("Deliberate", IsolationStatus.InProcessRequested, null),
            ]));

        Assert.Contains("First", error.Message, StringComparison.Ordinal);
        Assert.Contains("Second", error.Message, StringComparison.Ordinal);

        // The requested one is not an offender and must not be counted as one.
        Assert.DoesNotContain("Deliberate", error.Message, StringComparison.Ordinal);
        Assert.Contains("2 benchmark groups", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>--strict-isolation</c> reaches <see cref="MeasurementOptions.RequireIsolation" />.
    /// </summary>
    /// <remarks>
    ///     It set a CLI field with no mapping onto the options, so the flag could only ever take the
    ///     expensive path - measure everything, audit the results, set an exit code - even though the
    ///     early-throw mechanism it wanted already existed. The two are the same request phrased at
    ///     different times.
    /// </remarks>
    [Fact]
    public void StrictIsolationFlag_TurnsOnRequireIsolation()
    {
        var (cliArgs, errors) = CliArgs.ParseCore(["--strict-isolation"]);

        Assert.Empty(errors);

        var options = MeasurementOverrides.FromCliArgs(cliArgs)
            .Apply(MeasurementOptions.Default with { RequireIsolation = false });

        Assert.True(options.RequireIsolation);
    }

    /// <summary>
    ///     And its absence leaves a programmatic setting alone, rather than imposing the default on any
    ///     run that happened to parse a command line.
    /// </summary>
    [Fact]
    public void NoStrictIsolationFlag_LeavesTheConfiguredValueAlone()
    {
        var (cliArgs, _) = CliArgs.ParseCore([]);

        var options = MeasurementOverrides.FromCliArgs(cliArgs)
            .Apply(MeasurementOptions.Default with { RequireIsolation = false });

        Assert.False(options.RequireIsolation);
    }

    /// <summary>
    ///     Harness mode decides isolatability for every class before measuring any of them, so a run
    ///     that cannot isolate fails without having measured a single benchmark.
    /// </summary>
    /// <remarks>
    ///     The counter is the point. The decision used to be made per class immediately before that
    ///     class launched, so classes 1..N-1 were already measured by the time class N's refusal was
    ///     discovered - which under a hard error is the difference between failing in a second and
    ///     failing after the whole run.
    /// </remarks>
    [Fact]
    public async Task Harness_RefusedRun_ThrowsBeforeMeasuringAnything()
    {
        GateFixtureOne.Invocations = 0;
        GateFixtureTwo.Invocations = 0;

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(RequiredIsolationTests).Assembly)
            .WithCategoryFilter(["require-isolation-gate"])

            // A live factory: the coordinator holds it, so no worker can reproduce it.
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation()
            .WithOptions(Fast);

        using var scope = FakeWorkerLauncher.Install(_ => throw new InvalidOperationException("must not launch"));
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync());

            // Both classes named, from the one pass - not one class per run.
            Assert.Contains(nameof(GateFixtureOne), error.Message, StringComparison.Ordinal);
            Assert.Contains(nameof(GateFixtureTwo), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(0, GateFixtureOne.Invocations);
        Assert.Equal(0, GateFixtureTwo.Invocations);
        Assert.Empty(scope.Launcher.Requests);
    }

    /// <summary>
    ///     Turning the requirement off restores the labelled fallback, and the harness now reads the
    ///     setting at all - <c>WithOptions(new MeasurementOptions { RequireIsolation = true })</c> used
    ///     to set a field nothing consulted.
    /// </summary>
    [Fact]
    public async Task Harness_WithRequireIsolationOff_FallsBackAndLabels()
    {
        GateFixtureOne.Invocations = 0;
        GateFixtureTwo.Invocations = 0;

        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(RequiredIsolationTests).Assembly)
            .WithCategoryFilter(["require-isolation-gate"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation()
            .WithOptions(Fast)

            // After WithOptions, which replaces the record wholesale.
            .WithRequireIsolation(false);

        // A worker is available, so the refusal is the live factory rather than a missing nbworker -
        // which is what this test is about. The test host deploys none of its own.
        using var scope = FakeWorkerLauncher.Install(_ => throw new InvalidOperationException("must not launch"));
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await harness.RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(IsolationStatus.InProcessLiveFixture, r.IsolationStatus));
        Assert.True(GateFixtureOne.Invocations > 0);
    }

    /// <summary>
    ///     An explicit <c>[IsolatedProcess]</c> that is denied says so, on the console and on the row.
    /// </summary>
    /// <remarks>
    ///     It used to be indistinguishable from a benchmark that never asked: same status, same label,
    ///     same message. An explicit request being refused is strictly more interesting than a default
    ///     being refused, because the author already decided this one mattered.
    /// </remarks>
    [Fact]
    public async Task DeniedExplicitIsolationRequest_IsDistinguishableFromADefaultOne()
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        harness.AddFromAssembly(typeof(RequiredIsolationTests).Assembly)
            .WithCategoryFilter(["require-isolation-denied"])
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!))
            .WithLaunchCount(1)
            .WithIsolation()
            .WithOptions(Fast)

            // After WithOptions, which replaces the record wholesale.
            .WithRequireIsolation(false);

        // A worker is available, so the refusal is the live factory rather than a missing nbworker -
        // which is what this test is about. The test host deploys none of its own.
        using var scope = FakeWorkerLauncher.Install(_ => throw new InvalidOperationException("must not launch"));
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await harness.RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Contains("[IsolatedProcess]", stderr.ToString(), StringComparison.Ordinal);

        var demanded = results.Single(r => r.Name.EndsWith(".Demanded", StringComparison.Ordinal));
        var ordinary = results.Single(r => r.Name.EndsWith(".Ordinary", StringComparison.Ordinal));

        Assert.Contains(demanded.Warnings, w => w.Contains("[IsolatedProcess]", StringComparison.Ordinal));
        Assert.DoesNotContain(ordinary.Warnings, w => w.Contains("[IsolatedProcess]", StringComparison.Ordinal));
    }

    /// <summary>
    ///     One member asking for both processes is refused rather than resolved. NB0015 catches it in
    ///     source; this catches the assemblies no analyzer ever saw.
    /// </summary>
    [Fact]
    public void ConflictingIsolationAttributes_AreRefusedAtDiscovery()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new BenchmarkDiscoverer().Discover(typeof(ErrorFixtures.ConflictingIsolationBenchmarks)));

        Assert.Contains("[InProcess]", error.Message, StringComparison.Ordinal);
        Assert.Contains("[IsolatedProcess]", error.Message, StringComparison.Ordinal);
    }
}

// Two classes so the discovery-time pass has something to report about more than one of them.
[BenchmarkCategory("require-isolation-gate")]
public class GateFixtureOne
{
    public static int Invocations;

    [Benchmark]
    public void Body() => Invocations++;
}

[BenchmarkCategory("require-isolation-gate")]
public class GateFixtureTwo
{
    public static int Invocations;

    [Benchmark]
    public void Body() => Invocations++;
}

[BenchmarkCategory("require-isolation-denied")]
public class DeniedIsolationRequestBenchmarks
{
    [Benchmark]
    [IsolatedProcess]
    public void Demanded()
    {
    }

    [Benchmark]
    public void Ordinary()
    {
    }
}
