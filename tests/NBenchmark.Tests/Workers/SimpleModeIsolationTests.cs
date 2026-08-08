using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Simple mode measured in a real worker process.
///     <para>
///         Simple mode is the entry point people reach for first, and historically the least
///         trustworthy: a lambda measured in whatever process happened to be running inherits that
///         process's JIT tiering. These tests prove that a lambda written here is located, bound and
///         measured in another process - and, just as importantly, that one which cannot be is
///         refused rather than approximated.
///     </para>
/// </summary>
[Collection(nameof(RealWorkerCollection))]
public sealed class SimpleModeIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public SimpleModeIsolationTests()
    {
        // The test host has no deployed worker beside it, so discovery would correctly report none.
        // Pointing at the built worker exercises everything except locator discovery, which is
        // covered separately.
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    /// <summary>
    ///     Asserts the measurement really happened in a worker, surfacing the fallback reason when it
    ///     did not. A bare status comparison would report "expected Isolated, got InProcessNoWorker"
    ///     and leave the actual cause - which the result already carries as a warning - unread.
    /// </summary>
    private static void AssertIsolated(BenchmarkResult result)
        => Assert.True(
            result.IsolationStatus == IsolationStatus.Isolated,
            $"expected an isolated measurement, got {result.IsolationStatus}. "
            + $"Warnings: {string.Join(" | ", result.Warnings)}");

    private static MeasurementOptions FastOptions => MeasurementOptions.Default with
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

    /// <summary>
    ///     A non-capturing lambda is measured in a worker under the default profile. This is the
    ///     behaviour change that makes Simple mode trustworthy: nothing about the call site moved.
    /// </summary>
    [Fact]
    public void Run_NonCapturingLambda_IsMeasuredInAWorker()
    {
        var result = Benchmark.Run(() => Thread.SpinWait(200), FastOptions, name: "spin");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);

        // Stamped by the measuring process from its own environment, so it describes reality.
        Assert.Equal("steady-state", result.RuntimeProfileName);
        Assert.Equal("tiered=off pgo=off r2r=off", result.RuntimeKnobs);

        Assert.True(result.Mean > 0);
        Assert.NotEmpty(result.RawSamples);
    }

    /// <summary>
    ///     A value-returning body keeps its exact delegate shape across the boundary. Adapting it
    ///     through a <c>Func&lt;object&gt;</c> would box the return value and charge the user a
    ///     per-operation allocation they never wrote - which is exactly the pollution the harness's
    ///     own discovery path still exhibits.
    /// </summary>
    [Fact]
    public void Run_ValueReturningLambda_IsNotBoxedByTheBoundary()
    {
        var result = Benchmark.Run(() => 42, FastOptions, name: "answer");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);

        // Zero, not 24 bytes: the worker binds Func<int>, not Func<object>.
        Assert.True(
            result.AllocMedian is null or 0,
            $"a body that allocates nothing reported {result.AllocMedian} B/op");
    }

    /// <summary>
    ///     The explicit <c>static</c> lambda form must isolate too. It is the case where the obvious
    ///     capture test - <c>Delegate.Target is null</c> - is most tempting and most wrong: Roslyn
    ///     emits it as an instance method on a cached closure singleton, so <c>Target</c> is
    ///     non-null even though the body promises to capture nothing.
    /// </summary>
    [Fact]
    public void Run_StaticLambda_IsMeasuredInAWorker()
    {
        var result = Benchmark.Run(static () => Thread.SpinWait(200), FastOptions, name: "static-spin");

        AssertIsolated(result);
        Assert.False(result.Errored, result.ErrorMessage);
    }

    /// <summary>
    ///     An async body is measured in a worker with its <c>Task</c> shape intact.
    /// </summary>
    [Fact]
    public async Task RunAsync_NonCapturingLambda_IsMeasuredInAWorker()
    {
        var result = await Benchmark.RunAsync(
            () => Task.CompletedTask, FastOptions, name: "async-noop");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);
    }

    /// <summary>
    ///     A capture the value of which cannot be sent is measured here and <b>labelled</b>, never
    ///     reconstructed. Reconstructing it was probed: it did not throw, it returned a plausible
    ///     number for the wrong value.
    /// </summary>
    /// <remarks>
    ///     A capture of ordinary data no longer lands here at all - the value is sent and the benchmark
    ///     is isolated, which <c>CapturedStateTransferTests</c> covers. What reaches this path now is a
    ///     capture whose behaviour is not determined by its contents, where sending it would be the
    ///     silent substitution the design refuses.
    /// </remarks>
    [Fact]
    public void Run_CapturingUnsendableState_FallsBackAndSaysSo()
    {
        var stream = Stream.Null;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        BenchmarkResult result;

        try
        {
            result = Benchmark.Run(() => stream.Length, FastOptions, name: "captured");
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.InProcessCapturedState, result.IsolationStatus);

        // The provenance is on the result, so it survives the console message scrolling away.
        Assert.Equal("host", result.RuntimeProfileName);

        var message = stderr.ToString();
        Assert.Contains("stream", message);
        Assert.Contains("captured", message);
        Assert.Contains(SimpleModeGuidance.SuppressEnvVar, message);
    }

    /// <summary>
    ///     The guidance names every offender, not just the first one to hit a given reason.
    /// </summary>
    /// <remarks>
    ///     It used to dedupe on the <see cref="IsolationStatus" /> alone, so a script with twenty
    ///     <c>Benchmark.Run</c> calls - fifteen of them refused for the same reason - printed one line
    ///     naming the first. A reader would fix the benchmark they were told about and have no reason
    ///     to think the other fourteen were affected, because nothing else in the output says so:
    ///     Simple mode returns a <see cref="BenchmarkResult" /> rather than rendering a table with an
    ///     isolation column.
    /// </remarks>
    [Fact]
    public void CaptureGuidance_NamesEveryOffender()
    {
        var spins = 200;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        try
        {
            for (var i = 0; i < 3; i++)
            {
                Benchmark.Run(() => Thread.SpinWait(spins), FastOptions, name: $"captured-{i}");
            }
        }
        finally
        {
            Console.SetError(priorError);
        }

        var message = stderr.ToString();

        Assert.Contains("captured-0", message);
        Assert.Contains("captured-1", message);
        Assert.Contains("captured-2", message);
    }

    /// <summary>
    ///     Still once per offender, though - repeating the same benchmark in a loop says nothing new.
    /// </summary>
    [Fact]
    public void CaptureGuidance_IsEmittedOncePerOffender()
    {
        var spins = 200;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        try
        {
            for (var i = 0; i < 3; i++)
            {
                Benchmark.Run(() => Thread.SpinWait(spins), FastOptions, name: "captured-same");
            }
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(1, stderr.ToString().Split(SimpleModeGuidance.SuppressEnvVar).Length - 1);
    }

    /// <summary>
    ///     A loop large enough to flood a terminal is bounded, and says that it was - silence past the
    ///     cap would be indistinguishable from there being nothing more to report.
    /// </summary>
    [Fact]
    public void CaptureGuidance_IsBounded_AndSaysSo()
    {
        var spins = 200;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        try
        {
            for (var i = 0; i < 14; i++)
            {
                Benchmark.Run(() => Thread.SpinWait(spins), FastOptions, name: $"flood-{i}");
            }
        }
        finally
        {
            Console.SetError(priorError);
        }

        var message = stderr.ToString();

        Assert.Contains("further notes are suppressed", message);
        Assert.DoesNotContain("flood-13", message);
    }

    /// <summary>
    ///     <c>RunInProcess</c> measures here on purpose, with no warning and no worker. It is the
    ///     correct choice for cold-start work, where disabling tiering would measure the wrong thing
    ///     entirely - which is why the runtime profile is selectable rather than fixed.
    /// </summary>
    [Fact]
    public void RunInProcess_MeasuresHere_Deliberately_AndSilently()
    {
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        BenchmarkResult result;

        try
        {
            result = Benchmark.RunInProcess(() => Thread.SpinWait(200), FastOptions, name: "in-process");
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(IsolationStatus.InProcessRequested, result.IsolationStatus);
        Assert.Equal("host", result.RuntimeProfileName);
        Assert.False(result.Errored, result.ErrorMessage);

        // Deliberate choices do not get warned about.
        Assert.DoesNotContain("Isolation:", stderr.ToString());
    }

    /// <summary>
    ///     With no worker deployed at all, Simple mode still works - less accurately - and says why.
    ///     A packaging problem must not fail a measurement outright.
    /// </summary>
    [Fact]
    public void Run_WithNoWorkerDeployed_FallsBackAndSaysSo()
    {
        using var _ = FakeWorkerLauncher.InstallUnavailable();
        SimpleModeGuidance.ResetForTesting();

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        BenchmarkResult result;

        try
        {
            result = Benchmark.Run(() => Thread.SpinWait(200), FastOptions, name: "no-worker");
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.InProcessNoWorker, result.IsolationStatus);
        Assert.Contains("nbworker", stderr.ToString());
    }

    // ---------- Prepared state ----------

    /// <summary>
    ///     A benchmark over prepared data is isolated when the preparation is its own delegate.
    /// </summary>
    /// <remarks>
    ///     The contrast with <see cref="Run_CapturingUnsendableState_FallsBackAndSaysSo" /> is the whole point:
    ///     the same benchmark, over the same data, isolated or not depending only on whether the data
    ///     arrives as a captured value or as a recipe the worker can follow.
    /// </remarks>
    [Fact]
    public void Run_WithPreparedState_IsMeasuredInAWorker()
    {
        var result = Benchmark.Run(
            prepare: () => Enumerable.Range(0, 512).Reverse().ToArray(),
            body: data => data[^1],
            FastOptions,
            name: "prepared");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);

        Assert.Equal("steady-state", result.RuntimeProfileName);
        Assert.NotEmpty(result.RawSamples);
    }

    /// <summary>
    ///     The prepare delegate runs in the worker, not here, and runs exactly once.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both halves are load-bearing and neither is observable directly across a process
    ///         boundary, so the benchmark's own cost carries the evidence: the state is the spin count,
    ///         so a body measured over unprepared state would be immeasurably fast.
    ///     </para>
    ///     <para>
    ///         "Exactly once" matters because the alternative is invisible. Building the state per
    ///         invocation would still produce a plausible number - it would simply include the cost of
    ///         preparation in every reading, which is what a benchmark over prepared data exists to
    ///         exclude. The counter proves the worker did not do that.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Run_WithPreparedState_BuildsStateOnceInTheWorker()
    {
        var result = Benchmark.Run(
            prepare: () => PreparedStateProbe.Build(),
            body: spins => Thread.SpinWait(spins),
            FastOptions,
            name: "prepared-once");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);

        Assert.True(
            result.Median > 10_000,
            $"expected the worker to have built the state before measuring, but the body cost "
            + $"{result.Median:F1} ns - which is what an unprepared state would produce");

        // This process never built it, which is the other half of the same claim.
        Assert.Equal(0, PreparedStateProbe.Builds);
    }

    /// <summary>
    ///     A prepare delegate that captures is refused, exactly as a capturing body is.
    /// </summary>
    /// <remarks>
    ///     Splitting the shape is only worth anything if both halves are held to the same rule. A
    ///     capturing factory would leave the value in this process while claiming to be a recipe for it,
    ///     which is the reconstruction hazard wearing a different hat.
    /// </remarks>
    [Fact]
    public void Run_WithCapturingPrepare_FallsBackAndSaysSo()
    {
        var size = 512;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        BenchmarkResult result;

        try
        {
            result = Benchmark.Run(
                prepare: () => new int[size],
                body: data => data.Length,
                FastOptions,
                name: "captured-prepare");
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.InProcessCapturedState, result.IsolationStatus);

        var message = stderr.ToString();
        Assert.Contains("prepare delegate", message);
        Assert.Contains("captures", message);
    }

    /// <summary>
    ///     The fallback path builds the state here, once, and outside the timed region.
    /// </summary>
    /// <remarks>
    ///     With no worker available there is nothing to address, so the whole thing is measured in this
    ///     process - and must still honour the contract that preparation is not part of the reading.
    /// </remarks>
    [Fact]
    public void Run_WithPreparedState_AndNoWorker_BuildsStateOnceHere()
    {
        using var _ = FakeWorkerLauncher.InstallUnavailable();
        SimpleModeGuidance.ResetForTesting();

        var builds = 0;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        BenchmarkResult result;

        try
        {
            result = Benchmark.Run(
                prepare: () =>
                {
                    builds++;

                    return 200_000;
                },
                body: spins => Thread.SpinWait(spins),
                FastOptions,
                name: "prepared-fallback");
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.InProcessNoWorker, result.IsolationStatus);

        Assert.Equal(1, builds);
        Assert.True(result.Median > 10_000, $"body measured {result.Median:F1} ns");
    }

    /// <summary>
    ///     <c>RunInProcess</c> accepts prepared state too, so opting into the host process does not cost
    ///     the shape.
    /// </summary>
    [Fact]
    public void RunInProcess_WithPreparedState_MeasuresHereWithoutComplaint()
    {
        var builds = 0;

        var result = Benchmark.RunInProcess(
            prepare: () =>
            {
                builds++;

                return 512;
            },
            body: size => size + 1,
            FastOptions,
            name: "prepared-here");

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.InProcessRequested, result.IsolationStatus);
        Assert.Equal(1, builds);
    }
}

/// <summary>
///     A prepare delegate the worker can address, counting builds in whichever process runs it.
/// </summary>
/// <remarks>
///     Static so the lambda naming it captures nothing. The counter is per-process by construction,
///     which is exactly what makes it evidence about <i>where</i> preparation happened.
/// </remarks>
internal static class PreparedStateProbe
{
    public static int Builds;

    public static int Build()
    {
        Builds++;

        return 200_000;
    }
}

/// <summary>
///     Serializes the tests that swap the process-wide worker launcher, so one test's substitution
///     cannot leak into another running beside it.
/// </summary>
[CollectionDefinition(nameof(RealWorkerCollection), DisableParallelization = true)]
public sealed class RealWorkerCollection;

/// <summary>
///     The real process-spawning launcher, pointed at a known worker path. Used where the point of
///     the test is the process boundary rather than how the worker was found on disk.
/// </summary>
internal sealed class RealWorkerLauncher(string workerAssemblyPath) : IWorkerLauncher
{
    public bool IsAvailable => true;

    public async Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
        RunGroupPayload request,
        IBenchmarkProgress progress,
        IMeasurementObserver observer,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await using var worker = await WorkerHost
            .StartAsync(workerAssemblyPath, request.Options.RuntimeProfile, cancellationToken)
            .ConfigureAwait(false);

        return await WorkerGroupRunner
            .RunAsync(worker, request, progress, observer, timeout, cancellationToken)
            .ConfigureAwait(false);
    }
}
