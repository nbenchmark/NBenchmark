using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark.Attributes;

namespace NBenchmark.Tests.IsolationFixture;

/// <summary>
///     A real benchmark program, built as a real executable, so the isolation tests can spawn
///     a real child process. Every other isolation test in the repo substitutes
///     <c>FakeProcessLauncher</c>, which is why the composite-key defect that silently emptied
///     <c>RawSamples</c> on every isolated Harness result was able to ship: no test had ever
///     exercised the actual launcher end to end.
///     <para>
///         This cannot live in the test assembly. Isolation re-runs the entry assembly, and
///         under <c>dotnet test</c> that is the test host, not a program containing benchmarks.
///     </para>
/// </summary>
public static class Program
{
    public static Task Main(string[] args)
        => BenchmarkHarness.Create(args)
            .AddFromAssembly<IsolationFixtureBenchmarks>()
            .RunAsync();
}

/// <summary>
///     Two benchmarks with a deliberately large, stable cost difference, so a significance
///     test over them yields a non-null p-value and a non-negligible effect size. Both are
///     pure CPU work with no allocation, so nothing here depends on GC timing.
/// </summary>
public class IsolationFixtureBenchmarks
{
    [Benchmark(Baseline = true)]
    public void Fast() => Thread.SpinWait(200);

    [Benchmark]
    public void Slow() => Thread.SpinWait(2_000);
}

/// <summary>
///     The same two benchmarks under <see cref="InstanceLifetime.PerClass" />, which routes the
///     child through <c>RunPerClassHostChildAsync</c> instead of <c>RunPerMethodHostChildAsync</c>.
///     Both wrote the child payload and both had the same sample-key defect, so both need
///     coverage - a test against only the default lifetime leaves half the bug alive.
/// </summary>
[InstanceLifetime(InstanceLifetime.PerClass)]
public class SharedInstanceBenchmarks
{
    [Benchmark(Baseline = true)]
    public void Fast() => Thread.SpinWait(200);

    [Benchmark]
    public void Slow() => Thread.SpinWait(2_000);
}

/// <summary>
///     A benchmark that never returns, so the launcher's timeout and process-tree kill can be
///     tested against a genuinely wedged child rather than a mock.
///     <para>
///         It lives in its own class so it is only ever reached when a test names it explicitly
///         in a worker run request. Any end-to-end run of this fixture must exclude it
///         with <c>--filter IsolationFixtureBenchmarks.*</c>.
///     </para>
/// </summary>
public class HangingBenchmarks
{
    [Benchmark]
    public void Hang() => Thread.Sleep(Timeout.Infinite);
}

/// <summary>
///     A body slow enough per operation that a group of them takes far longer than an orphaned worker
///     should survive, so "stopped early" is distinguishable from "ran to completion" by wall clock.
///     <para>
///         Unlike <see cref="HangingBenchmarks" /> it <i>returns</i> between samples, which is where
///         the measurement loop observes cancellation. A body that never returns would prove nothing
///         about a worker noticing it had been orphaned - it would simply be killed as a wedged child.
///     </para>
///     <para>
///         Its own class, and only ever reached when a test names it explicitly in a worker run
///         request. Any end-to-end run of this fixture must exclude it with
///         <c>--filter IsolationFixtureBenchmarks.*</c>.
///     </para>
/// </summary>
public class LongGroupBenchmarks
{
    [Benchmark]
    public void Tick() => Thread.Sleep(25);
}

/// <summary>
///     A <c>[BenchmarkPlan]</c> factory reachable by <b>name</b>, for the addressing mode a
///     multi-runtime run depends on.
/// </summary>
/// <remarks>
///     Name addressing exists because each runtime's assembly is a separate build, in which a
///     metadata token from the coordinator's build identifies nothing. Until this fixture existed the
///     mode had no automated coverage at all - the only thing exercising it was
///     <c>samples/MultiRuntimeSuite</c>, run by hand - so a change to the resolver could pass every
///     test and break every multi-runtime run. Addressed here against the same build, which tests the
///     resolution mechanism without paying for three <c>dotnet build</c> invocations.
/// </remarks>
public static class NamedPlanFixture
{
    public const string SuiteName = "named-plan";

    public const string BenchmarkName = "only";

    [BenchmarkPlan]
    public static BenchmarkSuite BuildSuite() =>
        new BenchmarkSuite(SuiteName)
            .Add(BenchmarkName, () => Thread.SpinWait(200))
            .WithIterations(8)
            .WithWarmup(1)
            .WithOpsPerSample(1)
            .WithAutoTune(AutoTuneOptions.Default with
            {
                MaxTuningTime = TimeSpan.FromSeconds(5),
                MinWarmupTime = TimeSpan.Zero,
                MinMeasurementTime = TimeSpan.Zero,
                RequireJitQuiescence = false,
                EnableJitterCalibration = false,
            });

    /// <summary>Returns the wrong type, so the resolver's shape check has something to reject.</summary>
    public static string NotASuite() => "not a suite";
}

/// <summary>
///     Benchmark classes resolved from a container the worker builds itself, for the scoped and
///     unscoped dependency-injection paths.
/// </summary>
/// <remarks>
///     <para>
///         The signal is deliberately a <b>throw</b> rather than a counter, because the assertion has
///         to be made in the worker: the coordinator is a different process and cannot see how many
///         scopes were created. <see cref="ScopedClaim" /> is registered <c>AddScoped</c> and can be
///         claimed once, so a run that gives each benchmark instance its own scope succeeds and one
///         that resolves from the root - where a scoped registration behaves like a singleton - fails
///         the second benchmark and says why.
///     </para>
///     <para>
///         The claim is taken in <c>[BenchmarkSetup]</c>, which runs once per instance. Taking it in
///         the body would throw on the second iteration of the first benchmark, which measures
///         nothing about scoping.
///     </para>
/// </remarks>
public static class ScopedDiFixture
{
    public const string BenchmarkClassName = "ScopedDiBenchmarks";

    public static IServiceProvider BuildServices() => new ServiceCollection()
        .AddScoped<ScopedClaim>()
        .AddTransient<ScopedDiBenchmarks>()
        .BuildServiceProvider();
}

/// <summary>A scoped service that can be claimed exactly once.</summary>
public sealed class ScopedClaim
{
    private bool _claimed;

    public void Claim()
    {
        if (_claimed)
        {
            throw new InvalidOperationException(
                "this scoped service had already been claimed by another benchmark, so both were "
                + "resolved from the same scope");
        }

        _claimed = true;
    }
}

public class ScopedDiBenchmarks(ScopedClaim claim)
{
    [BenchmarkSetup]
    public void Setup() => claim.Claim();

    [Benchmark]
    public void First() => Thread.SpinWait(50);

    [Benchmark]
    public void Second() => Thread.SpinWait(50);
}

/// <summary>
///     A benchmark class with no parameterless constructor, built by an addressed instance factory.
/// </summary>
/// <remarks>
///     The constructor argument is the assertion. If the worker fell back to
///     <c>Activator.CreateInstance</c> - the substitution the whole instance-source design exists to
///     refuse - instantiation would fail and the group would fault, so a successful measurement is
///     proof the user's own factory ran in the measuring process.
/// </remarks>
public static class InstanceFactoryFixture
{
    public const string BenchmarkClassName = "FactoryBuiltBenchmarks";

    public static object Create(Type type) =>
        type == typeof(FactoryBuiltBenchmarks)
            ? new FactoryBuiltBenchmarks(marker: 42)
            : throw new InvalidOperationException($"unexpected type '{type.FullName}'");
}

public class FactoryBuiltBenchmarks(int marker)
{
    [Benchmark]
    public int Measure()
    {
        if (marker != 42)
            throw new InvalidOperationException("the instance was not built by the addressed factory");

        // Spin so one sample spans measurably more than a single clock step, matching every other
        // fixture body in this file that is measured with OpsPerSample pinned to 1
        // (NamedPlanFixture spins 200, ScopedDiBenchmarks spins 50). Without it this body is an int
        // comparison and a return - a couple of nanoseconds - and on a host whose timer steps in
        // coarse units (41.667 ns on Apple Silicon, 100 ns on Windows QPC) a single-invocation sample
        // reads either zero or one whole step depending on where the tick boundary happens to fall.
        // The test asserts Median > 0, and with four such samples the median is zero whenever three
        // of them miss a tick, which made it fail on roughly one run in six.
        //
        // The spin is the fix rather than a larger OpsPerSample because the options are shared with
        // the sibling tests in InstanceSourceIsolationTests, and rather than dropping the assertion
        // because "it measured real time" is part of what a successful row is meant to prove.
        Thread.SpinWait(200);

        return marker;
    }
}

/// <summary>
///     A class whose second method fails if it can see what the first left behind, so the instance
///     lifetime the worker ran under is readable from the results.
/// </summary>
/// <remarks>
///     The coordinator decides the lifetime and sends it; the worker cannot be asked what it did,
///     because it is a different process and it has already exited by the time anything is asserted.
///     A fixture that throws on the sharing is the only way to observe from the outside which of the
///     two lifetimes was actually in effect - and the throw is the contamination itself, not a proxy
///     for it.
/// </remarks>
[InstanceLifetime(InstanceLifetime.PerClass)]
public class InstanceSharingProbeBenchmarks
{
    private int _touched;

    [Benchmark]
    public int First() => ++_touched;

    [Benchmark]
    public int Second()
        => _touched == 0
            ? 0
            : throw new InvalidOperationException(
                "this instance had already run First, so both methods shared one instance");
}

/// <summary>
///     A benchmark whose body hard-exits the worker process mid-group, so the crash-resilience
///     path can be exercised against a genuinely dead child rather than a mock.
/// </summary>
/// <remarks>
///     <para>
///         Two methods, in declaration order. <c>Safe</c> completes and (once results are sent
///         incrementally per benchmark) has its result on the wire before <c>Crash</c> runs, so a
///         test can assert that an already-sent result survives a later benchmark's crash - the
///         property the plan calls out as the real amplifier. The group request must set
///         <see cref="RunOrder.Declaration" /> so <c>Safe</c> is measured first.
///     </para>
///     <para>
///         <c>Crash</c> writes a marker line to stderr and then exits with a fixed code, so a test
///         can assert both that the exit cause is named and that the stderr the worker produced
///         actually reaches the user. <c>Environment.Exit</c> is used rather than
///         <c>Environment.FailFast</c> because the exit code is the thing being asserted, and
///         <c>FailFast</c>'s code is runtime- and platform-specific. The stderr line is flushed
///         before the exit so the coordinator's async read is not racing the process death.
///     </para>
///     <para>
///         Its own class, and only ever reached when a test names it explicitly in a worker run
///         request. Any end-to-end run of this fixture must exclude it with
///         <c>--filter IsolationFixtureBenchmarks.*</c>.
///     </para>
/// </remarks>
public class HardExitBenchmarks
{
    public const string StderrMarker = "hard-exit fixture: crashing the worker with exit code 70";

    [Benchmark]
    public void Safe() => Thread.SpinWait(200);

    [Benchmark]
    public void Crash()
    {
        Console.Error.WriteLine(StderrMarker);
        Console.Error.Flush();

        // Exit code 70 is the worker's own "crashed" code. Hard-coded here because
        // WorkerExitCode is internal to NBenchmark and this fixture is a separate assembly.
        Environment.Exit(70);
    }
}

/// <summary>
///     A benchmark whose body stack-overflows, so the torn-frame and crash-cause paths can be
///     exercised against a worker that dies hard while writing, not a worker that exits politely.
/// </summary>
/// <remarks>
///     <para>
///         A stack overflow is the reachable torn-frame case the plan calls out: the worker dies
///         without finishing any in-flight frame, and the coordinator's read lands on a truncated
///         payload. Whether the death reads as a torn frame (<c>EndOfStreamException</c>) or a clean
///         end (<c>null</c>) depends on timing, so the test that uses this fixture asserts only that
///         the run <i>does not terminate with an unhandled exception</i> either way - both the
///         torn-frame catch and the clean-end path must produce a fault.
///     </para>
///     <para>
///         The recursion deliberately allocates on each frame so the stack exhausts in a bounded
///         number of calls rather than spinning for an unbounded time against a tiny per-frame cost.
///         The result is ignored so the JIT cannot tail-call it away.
///     </para>
///     <para>
///         Its own class, and only ever reached when a test names it explicitly in a worker run
///         request. Any end-to-end run of this fixture must exclude it with
///         <c>--filter IsolationFixtureBenchmarks.*</c>.
///     </para>
/// </remarks>
public class StackOverflowBenchmarks
{
    [Benchmark]
    public void Overflow() => Recurse(0);

    private static int Recurse(int depth)
    {
        // A per-frame allocation so the stack exhausts quickly and deterministically, and a use
        // of the result so the call is not tail-call-eliminated.
        var pad = new byte[512];
        return pad.Length == 0 || depth == int.MaxValue ? 0 : Recurse(depth + 1) + pad[0];
    }
}
