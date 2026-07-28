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
    ///     A capturing body is measured here and <b>labelled</b>, never reconstructed. Reconstructing
    ///     it was probed: it did not throw, it returned a plausible number for the wrong value. A
    ///     mechanism that is right most of the time and silently wrong the rest is worse than one
    ///     that declines.
    /// </summary>
    [Fact]
    public void Run_CapturingLambda_FallsBackAndSaysSo()
    {
        var spins = 200;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        BenchmarkResult result;

        try
        {
            result = Benchmark.Run(() => Thread.SpinWait(spins), FastOptions, name: "captured");
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
        Assert.Contains("captures", message);
        Assert.Contains("captured", message);
        Assert.Contains(SimpleModeGuidance.SuppressEnvVar, message);
    }

    /// <summary>
    ///     The guidance is once per process per reason, not once per call. Simple mode is used in
    ///     loops; a message on every call is a message people stop reading.
    /// </summary>
    [Fact]
    public void CaptureGuidance_IsEmittedOnce_AndIsSuppressible()
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

        Assert.Equal(1, stderr.ToString().Split(SimpleModeGuidance.SuppressEnvVar).Length - 1);
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
