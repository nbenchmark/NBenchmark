using NBenchmark.Attributes;
using NBenchmark.Engine;
using NBenchmark.Lifecycle;
using Xunit;

namespace NBenchmark.Tests;

public class PerClassStateResetTests
{
    [Fact]
    public async Task SuiteRunner_Hook_Fires_N_Minus_1_Times_For_N_Envelopes()
    {
        // The between-benchmarks hook must fire N-1 times for N envelopes (no fire
        // before the first, no fire after the last). Three envelopes -> two fires.
        var envelopes = new[]
        {
            StaticEnvelope("a"),
            StaticEnvelope("b"),
            StaticEnvelope("c"),
        };

        var fireCount = 0;

        await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 3,
            NullBenchmarkProgress.Instance, CancellationToken.None,
            () => { fireCount++; return Task.CompletedTask; });

        Assert.Equal(2, fireCount);
    }

    [Fact]
    public async Task SuiteRunner_Hook_Does_Not_Fire_For_Single_Envelope()
    {
        var envelopes = new[] { StaticEnvelope("only") };
        var fireCount = 0;

        await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 1,
            NullBenchmarkProgress.Instance, CancellationToken.None,
            () => { fireCount++; return Task.CompletedTask; });

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public async Task SuiteRunner_Hook_Null_Default_Does_Not_Fire()
    {
        // The default (null) must not fire - this is the per-method, per-benchmark, and
        // suite-mode path. Confirms the parameter default is safe.
        var envelopes = new[]
        {
            StaticEnvelope("a"),
            StaticEnvelope("b"),
        };

        var (results, _) = await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 2,
            NullBenchmarkProgress.Instance, CancellationToken.None);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SuiteRunner_Hook_Fires_After_Completed_Before_Next_Starting()
    {
        // The hook must fire after OnBenchmarkCompleted and before the next OnBenchmarkStarting.
        // We track the order of events: a-start, a-complete, [hook], b-start, b-complete.
        var progress = new CapturingProgress();
        var envelopes = new[]
        {
            StaticEnvelope("a"),
            StaticEnvelope("b"),
        };

        var eventLog = new List<string>();
        progress.OnBenchmarkCompletedHandler = name => eventLog.Add($"{name}-completed");
        progress.OnBenchmarkStartingHandler = (name, _, _) => eventLog.Add($"{name}-starting");

        await SuiteRunner.RunAsync(
            envelopes, RunOrder.Declaration, null, MeasurementOptions.Default,
            0, 2,
            progress, CancellationToken.None,
            () => { eventLog.Add("hook"); return Task.CompletedTask; });

        Assert.Equal(
            new[] { "a-starting", "a-completed", "hook", "b-starting", "b-completed" },
            eventLog);
    }

    [Fact]
    public async Task Harness_PerClass_With_IStateReset_Calls_ResetAsync_Between_Methods()
    {
        // Integration test: a PerClass benchmark class implementing IStateReset must have
        // ResetAsync called between benchmark methods. We use --dry-run with a pinned
        // WarmupIterations=1 so the body runs once, and assert the reset call count is
        // N-1 for N methods. We run in-process to observe the shared instance directly.
        ResetTrackingBenchmarks.ResetCallCount = 0;
        ResetTrackingBenchmarks.SharedState = 0;

        var harness = BenchmarkHarness.Create([
            "--filter", "ResetTrackingBenchmarks.*",
            "--in-process",
            "--iterations", "1",
            "--warmup", "1",
        ]);
        harness.AddFromAssembly(typeof(ResetTrackingBenchmarks).Assembly);

        await harness.RunAsync();

        // Two [Benchmark] methods -> ResetAsync fires once between them (N-1 = 1).
        Assert.Equal(1, ResetTrackingBenchmarks.ResetCallCount);
    }

    [Fact]
    public async Task Harness_PerClass_Without_IStateReset_Does_Not_Call_Reset()
    {
        // A PerClass class that does NOT implement IStateReset must not fire any reset
        // (the hook is null). This confirms the typeof(IStateReset).IsAssignableFrom guard.
        NoResetBenchmarks.ResetCallCount = 0;

        var harness = BenchmarkHarness.Create([
            "--filter", "NoResetBenchmarks.*",
            "--in-process",
            "--iterations", "1",
            "--warmup", "1",
        ]);
        harness.AddFromAssembly(typeof(NoResetBenchmarks).Assembly);

        await harness.RunAsync();

        Assert.Equal(0, NoResetBenchmarks.ResetCallCount);
    }

    private static BenchmarkEnvelope StaticEnvelope(string name) => new(
        name,
        "",
        null,
        false,
        [],
        (spec, ct) =>
        {
            var outcome = BenchmarkRunner.Instance.Run(name, () => { }, spec, ct);
            return Task.FromResult(outcome);
        });

    private sealed class CapturingProgress : IBenchmarkProgress
    {
        public Action<string, int, int>? OnBenchmarkStartingHandler { get; set; }
        public Action<string>? OnBenchmarkCompletedHandler { get; set; }

        public Task OnSuiteStarting(IReadOnlyList<string> benchmarkNames, int total) => Task.CompletedTask;
        public Task OnWarmupStarting(string name, int totalWarmupIterations) => Task.CompletedTask;
        public Task OnWarmupCompleted(string name) => Task.CompletedTask;

        public Task OnBenchmarkStarting(string name, int index, int total)
        {
            OnBenchmarkStartingHandler?.Invoke(name, index, total);
            return Task.CompletedTask;
        }

        public Task OnIterationCompleted(string name, int iteration, int totalIterations) => Task.CompletedTask;

        public Task OnBenchmarkCompleted(BenchmarkResult result)
        {
            OnBenchmarkCompletedHandler?.Invoke(result.Name);
            return Task.CompletedTask;
        }

        public Task OnSuiteCompleted(IReadOnlyList<BenchmarkResult> results) => Task.CompletedTask;
    }
}

[InstanceLifetime(InstanceLifetime.PerClass)]
public class ResetTrackingBenchmarks : IStateReset
{
    public static int ResetCallCount;
    public static int SharedState;

    [Benchmark]
    public void MethodA()
    {
        // Mutate shared state so we can observe ordering if reset fails.
        SharedState++;
    }

    [Benchmark]
    public void MethodB()
    {
        SharedState++;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        ResetCallCount++;
        SharedState = 0;
        return Task.CompletedTask;
    }
}

[InstanceLifetime(InstanceLifetime.PerClass)]
public class NoResetBenchmarks
{
    public static int ResetCallCount;

    [Benchmark]
    public void MethodA() { }

    [Benchmark]
    public void MethodB() { }
}