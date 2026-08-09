using System.Diagnostics;
using NBenchmark.Attributes;
using NBenchmark.Engine;
using NBenchmark.Reporters;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     What the <c>--verify-isolation</c> comparison pass is allowed to touch.
/// </summary>
/// <remarks>
///     <para>
///         The pass re-runs the whole harness in-process so the argument for isolating is made against
///         the user's own code. It previously did that by overwriting four fields and restoring them
///         afterwards, which leaked through every publish decision driven by a field it had not thought
///         of. These tests pin the ones that leaked: the user's observer received a second full suite
///         stream and was disposed twice, a second suite activity opened under the same name, and the
///         refusal-dedup set was never cleared - so a second run of the same harness printed no
///         refusals at all.
///     </para>
///     <para>
///         Deliberately asserted from the outside - what the observer saw, what the reporter was asked
///         to publish, how many activities opened - rather than by inspecting harness state. The
///         restructure replaced that state, and a test coupled to it would have had to be rewritten
///         alongside the thing it was supposed to be checking.
///     </para>
/// </remarks>
[Collection("ConsoleCapture")]
public sealed class VerifyIsolationPassTests
{
    private static BenchmarkHarness Harness(params string[] args)
    {
        var harness = BenchmarkHarness.Create(args);

        harness.AddFromAssembly(typeof(VerifyIsolationPassTests).Assembly)
            .WithCategoryFilter(["verify-iso"])
            .WithLaunchCount(1)
            .WithOptions(MeasurementOptions.Default with
            {
                // No worker is deployed beside the test host, so every class here is refused. These
                // tests are about the shape of the --verify-isolation pass - one suite stream, one
                // regression gate, reporters on the measured run only - not about the gate that would
                // otherwise stop the run before any of it happened.
                RequireIsolation = false,
                Iterations = 1,
                WarmupIterations = 0,
                OpsPerSample = 1,
                AutoTune = AutoTuneOptions.Default with
                {
                    MinWarmupTime = TimeSpan.Zero,
                    MinMeasurementTime = TimeSpan.Zero,
                    RequireJitQuiescence = false,
                    EnableJitterCalibration = false,
                },
            });

        return harness;
    }

    private static async Task<T> QuietlyAsync<T>(Func<Task<T>> action)
    {
        var priorOut = Console.Out;
        var priorError = Console.Error;

        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);

        try
        {
            return await action();
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetError(priorError);
        }
    }

    /// <summary>
    ///     One suite stream, and one dispose. The comparison pass used to resolve the very same observer
    ///     instance again from inside the primary pass's <c>using</c>, so a streaming observer saw two
    ///     run-end sentinels for one run and had its <c>Dispose</c> called twice - a double-finalise for
    ///     any observer whose dispose closes out a session.
    /// </summary>
    [Fact]
    public async Task VerifyIsolation_GivesTheObserverOneSuiteStream_AndDisposesItOnce()
    {
        var observer = new RecordingObserver();
        var harness = Harness("--verify-isolation");

        harness.WithObserver(observer);

        var results = await QuietlyAsync(() => harness.RunAsync());

        Assert.Equal(1, observer.SuiteCompletedCount);
        Assert.Equal(1, observer.DisposeCount);
        Assert.Equal(results.Count, observer.Results.Count);
    }

    /// <summary>
    ///     Two suite activities - the run and the diagnostic pass - and only one of them claims to be
    ///     the run.
    /// </summary>
    /// <remarks>
    ///     The comparison pass is labelled rather than suppressed on purpose. Its per-benchmark spans
    ///     are raised from deep inside the engine, where the pass is not visible, so suppressing only
    ///     the parent would leave them as parentless roots. A diagnostic pass must not impersonate the
    ///     run, but it may describe itself.
    /// </remarks>
    [Fact]
    public async Task VerifyIsolation_OpensOneSuiteActivityPerPass_AndLabelsTheComparison()
    {
        var suiteNames = new List<string?>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "NBenchmark",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "benchmark.suite")
                    suiteNames.Add(activity.GetTagItem("nbenchmark.suite.name") as string);
            },
        };

        ActivitySource.AddActivityListener(listener);

        await QuietlyAsync(() => Harness("--verify-isolation").RunAsync());

        Assert.Equal(2, suiteNames.Count);
        Assert.Single(suiteNames, n => n is not null && n.Contains("[in-process comparison]"));
        Assert.Single(suiteNames, n => n is not null && !n.Contains("[in-process comparison]"));
    }

    /// <summary>
    ///     The comparison pass exists to be compared against, not published. One reporter invocation for
    ///     the run, none for the pass.
    /// </summary>
    [Fact]
    public async Task VerifyIsolation_DoesNotInvokeReportersForTheComparisonPass()
    {
        var reporter = new CountingReporter();
        var harness = Harness("--verify-isolation");

        harness.WithReporter(reporter);

        await QuietlyAsync(() => harness.RunAsync());

        Assert.Equal(1, reporter.ReportCount);
    }

    /// <summary>
    ///     A diagnostic command must not change the build's outcome, and the regression gate must not
    ///     run twice over the same numbers.
    /// </summary>
    [Fact]
    public async Task VerifyIsolation_EvaluatesTheRegressionGateOnce()
    {
        var priorExitCode = Environment.ExitCode;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        var priorOut = Console.Out;

        Console.SetError(stderr);
        Console.SetOut(TextWriter.Null);

        try
        {
            await Harness("--verify-isolation", "--threshold-pct", "1").RunAsync();
        }
        finally
        {
            Console.SetOut(priorOut);
            Console.SetError(priorError);
            Environment.ExitCode = priorExitCode;
        }

        var occurrences = stderr.ToString().Split("Regression threshold exceeded").Length - 1;

        Assert.True(occurrences <= 1, $"the regression gate reported {occurrences} times");
    }

    /// <summary>
    ///     The results handed back are the ones the run measured, not the comparison pass's. Returning
    ///     the diagnostic numbers would be the worst available failure: they look like results.
    /// </summary>
    [Fact]
    public async Task VerifyIsolation_ReturnsTheMeasuredRunNotTheComparison()
    {
        var withVerify = await QuietlyAsync(() => Harness("--verify-isolation").RunAsync());
        var without = await QuietlyAsync(() => Harness().RunAsync());

        Assert.Equal(
            without.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal),
            withVerify.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>
    ///     Two runs of the same harness behave identically. The refusal-dedup set used to be a field
    ///     that was never cleared, so a second <c>RunAsync()</c> printed no isolation refusals at all -
    ///     and the comparison pass inherited the first pass's set.
    /// </summary>
    [Fact]
    public async Task VerifyIsolation_RunTwiceOnTheSameHarness_IsStableAndStreamsOncePerRun()
    {
        var observer = new RecordingObserver();
        var reporter = new CountingReporter();
        var harness = Harness("--verify-isolation");

        harness.WithObserver(observer).WithReporter(reporter);

        var first = await QuietlyAsync(() => harness.RunAsync());

        Assert.Equal(1, observer.SuiteCompletedCount);
        Assert.Equal(1, reporter.ReportCount);

        var second = await QuietlyAsync(() => harness.RunAsync());

        Assert.Equal(2, observer.SuiteCompletedCount);
        Assert.Equal(2, reporter.ReportCount);

        Assert.Equal(
            first.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal),
            second.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    private sealed class RecordingObserver : IMeasurementObserver
    {
        public List<BenchmarkResult> Results { get; } = [];
        public int SuiteCompletedCount { get; private set; }
        public int DisposeCount { get; private set; }

        public void OnPhase(in MeasurementPhaseEvent e)
        {
            if (e.Phase == MeasurementPhase.SuiteCompleted)
                SuiteCompletedCount++;
        }

        public void OnSample(in SampleEvent e)
        {
        }

        public void OnDetector(in DetectorStateEvent e)
        {
        }

        public void OnResult(BenchmarkResult result) => Results.Add(result);

        public void Dispose() => DisposeCount++;
    }

    private sealed class CountingReporter : IReporter
    {
        public int ReportCount { get; private set; }

        public string Name => "counting";

        public ReportDetail Detail { get; set; }

        public Task ReportAsync(
            IReadOnlyList<BenchmarkResult> results,
            CancellationToken cancellationToken = default)
        {
            ReportCount++;
            return Task.CompletedTask;
        }
    }
}

/// <summary>
///     Two trivial isolatable bodies, in their own category so the comparison pass has something to
///     measure twice without any other test's benchmarks being dragged in.
/// </summary>
[BenchmarkCategory("verify-iso")]
public class VerifyIsolationBenchmarks
{
    [Benchmark]
    public void Fast() => Thread.SpinWait(50);

    [Benchmark]
    public void Slow() => Thread.SpinWait(500);
}
