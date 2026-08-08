using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     An ordinary inline suite - no factory, no attribute, no change to how it is written - measured
///     in a worker.
///     <para>
///         This is the ergonomics guarantee. Requiring a <c>[BenchmarkPlan]</c> factory to get
///         accurate numbers would make the accurate path the inconvenient one, and people would
///         reasonably keep writing the convenient, quietly-wrong one. A plan is the escape hatch for
///         suites holding live objects a worker would have to be given rather than able to build.
///     </para>
/// </summary>
[Collection(nameof(RealWorkerCollection))]
public sealed class InlineSuiteIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public InlineSuiteIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static BenchmarkSuite Fast(BenchmarkSuite suite) => suite
        .WithIterations(16)
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

    /// <summary>
    ///     The plain shape people already write is now isolated. Nothing about the call moved.
    /// </summary>
    [Fact]
    public async Task InlineSuite_IsMeasuredInAWorker_WithNoCeremony()
    {
        var results = await Fast(new BenchmarkSuite("inline")
                .Add("fast", () => Thread.SpinWait(200))
                .Add("slow", () => Thread.SpinWait(2_000))
                .WithBaseline("fast"))
            .RunAsync();

        Assert.Equal(2, results.Count);

        foreach (var result in results)
        {
            Assert.False(result.Errored, result.ErrorMessage);
            Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
            Assert.Equal("steady-state", result.RuntimeProfileName);
            Assert.NotEmpty(result.RawSamples);
        }

        var fast = results.Single(r => r.Name == "fast");
        var slow = results.Single(r => r.Name == "slow");

        Assert.True(fast.IsBaseline);
        Assert.False(slow.IsBaseline);

        Assert.True(
            slow.Median > fast.Median * 2,
            $"expected slow to be clearly slower: fast={fast.Median:F1}ns slow={slow.Median:F1}ns");
    }

    /// <summary>
    ///     All the suite's benchmarks share one worker, so every ratio between them is a paired,
    ///     within-process comparison. Measuring each in its own process would turn every ratio into a
    ///     between-process contrast and inflate its variance for no accuracy gain - the dominant error
    ///     is the runtime configuration, which is per-process and identical here either way.
    /// </summary>
    [Fact]
    public async Task InlineSuite_MeasuresAllBenchmarksInOneWorker()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        var results = await Fast(new BenchmarkSuite("one-worker")
                .Add("a", () => Thread.SpinWait(200))
                .Add("b", () => Thread.SpinWait(200))
                .Add("c", () => Thread.SpinWait(200)))
            .RunAsync();

        Assert.Equal(3, results.Count);
        Assert.Equal(1, launcher.GroupsRun);
        Assert.Equal(3, launcher.LastBodyCount);
    }

    /// <summary>
    ///     <c>LaunchCount</c> is the replicate count, and each replicate is a fresh worker - which is
    ///     what produces a between-process reproducibility estimate rather than repeated measurements
    ///     inside one process.
    /// </summary>
    [Fact]
    public async Task InlineSuite_LaunchCount_SpawnsOneWorkerPerReplicate()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        var results = await Fast(new BenchmarkSuite("replicated")
                .Add("a", () => Thread.SpinWait(200)))
            .WithLaunchCount(3)
            .RunAsync();

        Assert.Equal(3, launcher.GroupsRun);

        var result = Assert.Single(results);
        Assert.NotNull(result.LaunchStatistics);
        Assert.Equal(3, result.LaunchStatistics!.LaunchCount);
    }

    /// <summary>
    ///     A body whose capture cannot be sent names itself in the refusal. A suite has several
    ///     benchmarks and only one of them is usually the problem, so a message that does not say which
    ///     is not actionable.
    /// </summary>
    /// <remarks>
    ///     A capture of ordinary data is sent and the suite stays isolated; what reaches this path is a
    ///     value whose behaviour is not determined by its contents.
    /// </remarks>
    [Fact]
    public async Task InlineSuite_UnsendableCapture_NamesTheBenchmarkAndSuggestsThePlan()
    {
        var stream = Stream.Null;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("captures")
                    .Add("clean", () => Thread.SpinWait(200))
                    .Add("dirty", () => stream.Length))
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        var message = stderr.ToString();
        Assert.Contains("dirty", message);
        Assert.Contains("captures", message);
        Assert.Contains("[BenchmarkPlan]", message);
    }

    /// <summary>
    ///     A suite setup that captures nothing is carried to the worker and run there, so the suite
    ///     keeps its isolation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The assertion that matters is the second one. Proving the setup <i>ran</i> is easy;
    ///         proving it ran <b>in the worker</b> needs the benchmark's own cost to depend on it, which
    ///         is what the shared static gives us: the setup writes it and the body reads it, and both
    ///         resolve against the same copy of the test assembly inside the worker's load context. A
    ///         setup that had run in the coordinator instead would have written a static this process
    ///         owns, leaving the worker's copy at zero and the body immeasurably fast.
    ///     </para>
    ///     <para>
    ///         This replaces a test that asserted the opposite - that any suite lifecycle refused
    ///         isolation outright. That behaviour keyed on a hook <i>existing</i> rather than on whether
    ///         it could cross, so the ordinary <c>WithSuiteSetup(() =&gt; Cache.Clear())</c> cost the
    ///         whole suite its isolation for no reason.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task InlineSuite_WithNonCapturingSuiteSetup_IsIsolated_AndRunsSetupInTheWorker()
    {
        var results = await Fast(new BenchmarkSuite("lifecycle")
                .Add("spin", () => Thread.SpinWait(WorkerVisibleState.Spins))
                .WithSuiteSetup(() => WorkerVisibleState.Spins = 200_000))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
        Assert.NotEmpty(result.RawSamples);

        // Zero spins costs single-digit nanoseconds; 200,000 costs tens of microseconds. Any median
        // above this floor is only reachable if the worker ran the setup before measuring.
        Assert.True(
            result.Median > 10_000,
            $"expected the worker's own setup to have raised the body's cost, but it measured "
            + $"{result.Median:F1} ns - which is what an unrun setup would produce");

        // Nothing wrote this process's copy, which is the other half of the same claim.
        Assert.Equal(0, WorkerVisibleState.Spins);
    }

    /// <summary>
    ///     A suite teardown that captures nothing is carried too, and runs after the group's work.
    /// </summary>
    [Fact]
    public async Task InlineSuite_WithNonCapturingSuiteTeardown_IsIsolated()
    {
        var results = await Fast(new BenchmarkSuite("teardown")
                .Add("a", () => Thread.SpinWait(200))
                .WithSuiteTeardown(() => WorkerVisibleState.Reset()))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
    }

    /// <summary>
    ///     A suite setup that captures is carried to the worker and shares the bodies' state.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The blanket refusal this replaces was right for as long as each address carried its own
    ///         copy of its receiver: a setup given a private copy would have prepared state the
    ///         benchmarks never see, which is silent and looks like a working suite. With the group's
    ///         receivers shared, the setup and the body are bound to the one object they closed over
    ///         here, so the preparation reaches the measurement.
    ///     </para>
    ///     <para>
    ///         The body asserting on what the setup wrote is the evidence. Proving the setup <i>ran</i>
    ///         would be satisfied by a private copy too.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task InlineSuite_WithCapturingSuiteSetup_IsIsolated_AndReachesTheBody()
    {
        var buffer = new int[16];

        var results = await Fast(new BenchmarkSuite("captured-lifecycle")
                .Add("a", () =>
                {
                    if (buffer[0] != 9)
                        throw new InvalidOperationException($"suite setup did not reach this body: {buffer[0]}");
                })
                .WithSuiteSetup(() => buffer[0] = 9))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);

        // Untouched here: the setup ran in the worker, on the worker's copy of the shared state.
        Assert.Equal(0, buffer[0]);
    }

    /// <summary>
    ///     A suite setup holding something that cannot be sent is still refused, and points at the plan.
    /// </summary>
    [Fact]
    public async Task InlineSuite_WithUnsendableSuiteSetup_RefusesAndPointsAtThePlan()
    {
        var stream = Stream.Null;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("captured-lifecycle")
                    .Add("a", () => Thread.SpinWait(200))
                    .WithSuiteSetup(() => _ = stream.Length))
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Single(results);

        Assert.All(
            results,
            r => Assert.Equal(IsolationStatus.InProcessCapturedState, r.IsolationStatus));

        var message = stderr.ToString();
        Assert.Contains("WithSuiteSetup", message);
        Assert.Contains("captures", message);
        Assert.Contains("[BenchmarkPlan]", message);
    }

    /// <summary>
    ///     Per-iteration hooks that capture nothing are addressed alongside their body, so the suite
    ///     keeps its isolation and the hooks run in the worker.
    /// </summary>
    [Fact]
    public async Task InlineSuite_WithNonCapturingIterationHooks_IsIsolated()
    {
        var results = await Fast(new BenchmarkSuite("hooks")
                .Add(
                    "a",
                    () => Thread.SpinWait(200),
                    setup: () => WorkerVisibleState.Touch(),
                    teardown: () => WorkerVisibleState.Touch()))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
        Assert.NotEmpty(result.RawSamples);
    }

    /// <summary>
    ///     State a worker's own lifecycle delegates can reach, for the tests above. Static because a
    ///     lambda touching it captures nothing and is therefore addressable - which is the point.
    /// </summary>
    internal static class WorkerVisibleState
    {
        public static int Spins;

        public static int Touches;

        public static void Touch() => Touches++;

        public static void Reset() => Spins = 0;
    }

    /// <summary>
    ///     <c>WithIsolation(false)</c> is a deliberate request for the host process, and is reported
    ///     as such rather than as a refusal the user should act on.
    /// </summary>
    [Fact]
    public async Task InlineSuite_WithIsolationFalse_MeasuresHereWithoutComplaint()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("opted-out")
                    .Add("a", () => Thread.SpinWait(200)))
                .WithIsolation(false)
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Single(results);
        Assert.Equal(0, launcher.GroupsRun);
        Assert.DoesNotContain("Isolation:", stderr.ToString());
    }

    /// <summary>
    ///     A parameter sweep is isolated, and each expanded variant is measured with its own value.
    /// </summary>
    /// <remarks>
    ///     The typed lambda <c>(int spins) =&gt; …</c> captures nothing and was always addressable; what
    ///     could not cross the boundary was only the wrapper NBenchmark built to bind the value. Sending
    ///     the value beside the address means the worker builds that wrapper, in the process that
    ///     measures it. The monotonic assertion is the load-bearing half: it proves each variant really
    ///     received <i>its own</i> argument rather than a default or a neighbour's - a mis-bound argument
    ///     would measure a different call and report it under the right name.
    /// </remarks>
    [Fact]
    public async Task InlineSuite_WithParameters_IsIsolated_AndBindsEachValue()
    {
        var results = await Fast(new BenchmarkSuite("sweep")
                .WithParameter("spins", 200, 20_000, 200_000)
                .Add("spin", (int spins) => Thread.SpinWait(spins))
                .WithRunOrder(RunOrder.Declaration))
            .RunAsync();

        Assert.Equal(3, results.Count);

        foreach (var result in results)
        {
            Assert.False(result.Errored, result.ErrorMessage);
            Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
            Assert.Equal("steady-state", result.RuntimeProfileName);
            Assert.NotEmpty(result.RawSamples);
        }

        var small = results.Single(r => r.Name.Contains("200)", StringComparison.Ordinal));
        var medium = results.Single(r => r.Name.Contains("20000)", StringComparison.Ordinal));
        var large = results.Single(r => r.Name.Contains("200000)", StringComparison.Ordinal));

        Assert.True(
            small.Median < medium.Median && medium.Median < large.Median,
            $"expected cost to rise with the bound argument: {small.Median:F1} < {medium.Median:F1} "
            + $"< {large.Median:F1} ns");
    }

    /// <summary>
    ///     Two parameters bind in declaration order, not reversed and not by position within the type.
    /// </summary>
    /// <remarks>
    ///     Worth its own test because a two-argument body is where an ordering mistake stops being
    ///     detectable: with both parameters the same type, swapping them still binds, still runs, and
    ///     still reports a number. The values here are deliberately asymmetric so the wrong order
    ///     produces the wrong ranking rather than the same one.
    /// </remarks>
    [Fact]
    public async Task InlineSuite_WithTwoParameters_BindsInDeclarationOrder()
    {
        var results = await Fast(new BenchmarkSuite("pair")
                .WithParameter("outer", 1, 40)
                .WithParameter("inner", 200)
                .Add("nested", (int outer, int inner) =>
                {
                    for (var i = 0; i < outer; i++)
                    {
                        Thread.SpinWait(inner);
                    }
                })
                .WithRunOrder(RunOrder.Declaration))
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));

        var one = results.Single(r => r.Name.Contains("outer=1,", StringComparison.Ordinal));
        var forty = results.Single(r => r.Name.Contains("outer=40,", StringComparison.Ordinal));

        Assert.True(
            forty.Median > one.Median * 4,
            $"expected outer=40 to cost clearly more than outer=1: {one.Median:F1} vs {forty.Median:F1} ns");
    }

    /// <summary>
    ///     A parameter whose declared type a worker cannot carry is refused by naming the parameter and
    ///     the type, not by blaming the suite.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Reaching this needs <c>object</c>, and the reason is worth writing down.
    ///         <c>WithParameter</c> already validates at registration - and validates the <i>runtime
    ///         type of each value</i>, so a genuinely exotic value is rejected there, before addressing
    ///         is ever consulted. Its permitted set (null, bool, the integer types, float/double/decimal,
    ///         char, string, enum) is a strict subset of what <see cref="TestArgumentCodec" /> can carry,
    ///         so for ordinary sweeps this check can never fire.
    ///     </para>
    ///     <para>
    ///         <c>WithParameter&lt;object&gt;("x", 200)</c> passes that validation - the value is a boxed
    ///         <c>int</c> - while the lambda's declared parameter is <c>object</c>, which the codec
    ///         refuses because it encodes against the declared type. So the guard is live rather than
    ///         defensive, and this is the shape that reaches it.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task InlineSuite_WithUnmarshallableParameterType_RefusesAndNamesTheParameter()
    {
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("opaque")
                    .WithParameter<object>("payload", 200)
                    .Add("consume", (object payload) => Thread.SpinWait((int)payload)))
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Single(results);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        var message = stderr.ToString();
        Assert.Contains("payload", message);
        Assert.Contains("Object", message);
        Assert.Contains("[BenchmarkPlan]", message);
    }

    /// <summary>
    ///     A parameterized body carrying per-iteration hooks is isolated, and the hooks travel with it.
    /// </summary>
    /// <remarks>
    ///     This is the regression guard for the defect that making parameter sweeps addressable
    ///     introduced. A parameterized registration never recorded whether it supplied
    ///     <c>setup</c>/<c>teardown</c> - harmless while it carried no addressable body, because it was
    ///     refused for that first. Once the body became addressable, an unrecorded hook meant the suite
    ///     was measured in a worker with its setup silently dropped: the numbers would look fine and
    ///     describe work that never happened. Asserted by having the body fail unless the setup reached
    ///     it, which is the only form that would catch a dropped hook.
    /// </remarks>
    [Fact]
    public async Task InlineSuite_ParameterizedWithIterationHooks_IsIsolated_AndKeepsTheHooks()
    {
        var flag = new int[1];

        var results = await Fast(new BenchmarkSuite("hooked")
                .WithParameter("spins", 200)
                .Add(
                    "spin",
                    (int spins) =>
                    {
                        if (flag[0] != 1)
                            throw new InvalidOperationException("the per-iteration setup did not run");

                        Thread.SpinWait(spins);
                    },
                    setup: () => flag[0] = 1))
            .RunAsync();

        var result = Assert.Single(results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
        Assert.Equal(0, flag[0]);
    }

    // ---------- Prepared state ----------

    /// <summary>
    ///     A suite over prepared state is isolated, and the state is built in the worker.
    /// </summary>
    /// <remarks>
    ///     This is the suite-shaped counterpart to the Simple-mode prepared-state path, and the reason it
    ///     matters more here: one worker measures the whole suite, so a single capturing body takes every
    ///     sibling in-process with it. Naming the preparation keeps the entire set isolated.
    /// </remarks>
    [Fact]
    public async Task StatefulSuite_IsIsolated_AndBuildsStateInTheWorker()
    {
        var results = await Fast(new BenchmarkSuite("stateful")
                .WithState(() => PreparedStateProbe.Build())
                .Add("spin", spins => Thread.SpinWait(spins))
                .Add("half", spins => Thread.SpinWait(spins / 2))
                .WithBaseline("spin"))
            .RunAsync();

        Assert.Equal(2, results.Count);

        foreach (var result in results)
        {
            Assert.False(result.Errored, result.ErrorMessage);
            Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
            Assert.NotEmpty(result.RawSamples);

            Assert.True(
                result.Median > 5_000,
                $"'{result.Name}' measured {result.Median:F1} ns, which is what unprepared state "
                + "would produce");
        }

        // Built in the worker, so this process's counter never moved.
        Assert.Equal(0, PreparedStateProbe.Builds);

        var spin = results.Single(r => r.Name == "spin");
        var half = results.Single(r => r.Name == "half");

        Assert.True(
            spin.Median > half.Median * 1.5,
            $"expected the full spin to cost clearly more than half: {spin.Median:F1} vs {half.Median:F1} ns");
    }

    /// <summary>
    ///     Each benchmark in a stateful suite gets its own prepared value, not one shared across the set.
    /// </summary>
    /// <remarks>
    ///     Per benchmark rather than per suite is a correctness choice, not an efficiency one. Two sorts
    ///     over one shared array would have the second measure what the first already sorted - and with
    ///     the default random run order, which one that is changes between runs, so the suite would report
    ///     a different answer each time for reasons nothing in the output explains.
    /// </remarks>
    [Fact]
    public async Task StatefulSuite_PreparesStatePerBenchmark()
    {
        var launcher = new CountingLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        WorkerLauncher.Current = launcher;

        var results = await Fast(new BenchmarkSuite("per-benchmark")
                .WithState(() => new int[64])
                .Add("a", buffer => buffer.Length)
                .Add("b", buffer => buffer.Length)
                .Add("c", buffer => buffer.Length))
            .RunAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));

        // Still one worker for the group, so the ratios stay paired within a process.
        Assert.Equal(1, launcher.GroupsRun);
        Assert.Equal(3, launcher.LastBodyCount);
    }

    /// <summary>
    ///     <c>WithState</c> after the suite is configured throws, rather than silently transplanting
    ///     settings into the new typed suite.
    /// </summary>
    /// <remarks>
    ///     The conversion cannot preserve configuration without copying roughly twenty fields, and a copy
    ///     that misses one presents as a setting that quietly stopped working. Refusing, with the corrected
    ///     call spelled out, is the honest version.
    /// </remarks>
    [Fact]
    public void WithState_AfterConfiguration_ThrowsAndSaysWhereToPutIt()
    {
        var suite = new BenchmarkSuite("late").Add("a", () => Thread.SpinWait(200));

        var ex = Assert.Throws<InvalidOperationException>(() => suite.WithState(() => 1));

        Assert.Contains("WithState must be called before", ex.Message);
        Assert.Contains("benchmarks have been", ex.Message);
        Assert.Contains("new BenchmarkSuite(\"late\").WithState(...)", ex.Message);
    }

    /// <summary>
    ///     A capturing state factory is refused, so the suite falls back rather than preparing state on
    ///     the wrong side of the boundary.
    /// </summary>
    [Fact]
    public async Task StatefulSuite_WithCapturingState_FallsBackAndSaysSo()
    {
        var size = 64;

        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await Fast(new BenchmarkSuite("captured-state")
                    .WithState(() => new int[size])
                    .Add("a", buffer => buffer.Length))
                .RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
        }

        Assert.Single(results);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        // The specific reason, not merely "not isolated". The suite computed this status and assigned
        // it to a field nothing read, so every fallback row kept BenchmarkResult's default -
        // InProcessRequested, which means "you asked for the host". A reader saw a run they had asked
        // to isolate reporting the status of one they had asked not to, with no remedy footer and no
        // Iso column. The old assertion here - NotEqual(Isolated) - passed against that default.
        Assert.All(
            results,
            r => Assert.Equal(IsolationStatus.InProcessCapturedState, r.IsolationStatus));

        var message = stderr.ToString();
        Assert.Contains("prepare delegate", message);
        Assert.Contains("captures", message);
    }

    /// <summary>
    ///     <c>WithRequireIsolation</c> turns a refusal into a throw, before anything is measured.
    /// </summary>
    /// <remarks>
    ///     The message carries the refusal verbatim rather than a bare "isolation was required". In this
    ///     mode the explanatory stderr line is never printed - the run does not get that far - so a
    ///     message without the cause would send the reader looking for output that does not exist.
    /// </remarks>
    [Fact]
    public async Task RequireIsolation_OnRefusal_ThrowsWithTheReason()
    {
        var spins = 200;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fast(new BenchmarkSuite("strict")
                    .Add("dirty", () => Thread.SpinWait(spins)))
                .WithRequireIsolation()
                .RunAsync());

        Assert.Contains("RequireIsolation is set", ex.Message);
        Assert.Contains("captures", ex.Message);
        Assert.Contains("prepare", ex.Message);
    }

    /// <summary>
    ///     <c>WithRequireIsolation</c> is silent when the suite can be isolated, which is the case it must
    ///     not disturb.
    /// </summary>
    [Fact]
    public async Task RequireIsolation_WhenIsolatable_RunsNormally()
    {
        var results = await Fast(new BenchmarkSuite("strict-ok")
                .Add("clean", () => Thread.SpinWait(200)))
            .WithRequireIsolation()
            .RunAsync();

        var result = Assert.Single(results);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
    }

    /// <summary>Counts worker groups so a test can assert how the work was partitioned.</summary>
    private sealed class CountingLauncher(string workerAssemblyPath) : IWorkerLauncher
    {
        private readonly RealWorkerLauncher _inner = new(workerAssemblyPath);

        public int GroupsRun { get; private set; }
        public int LastBodyCount { get; private set; }

        public bool IsAvailable => true;

        public Task<WorkerGroupRunner.GroupResult> RunGroupAsync(
            RunGroupPayload request,
            IBenchmarkProgress progress,
            IMeasurementObserver observer,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            GroupsRun++;
            LastBodyCount = request.Bodies.Count;

            return _inner.RunGroupAsync(request, progress, observer, timeout, cancellationToken);
        }
    }
}
