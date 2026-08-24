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
        SingleModeGuidance.ResetForTesting();
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
    ///     The same for a suite over prepared state. That this compiles at all is the parity guarantee:
    ///     every <c>With*</c> below returns the stateful suite, so the typed <c>Add</c> is still in
    ///     scope afterwards.
    /// </summary>
    private static BenchmarkSuite<TState> Fast<TState>(BenchmarkSuite<TState> suite) => suite
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
    ///     The same, with the hard-error gate turned off, for the tests that are about what the
    ///     <i>fallback</i> does and says.
    /// </summary>
    /// <remarks>
    ///     A refusal throws by default, so the labelled-fallback path and its stderr guidance are only
    ///     reachable this way - which is the setting a caller who prefers a labelled number to an
    ///     exception would use. The gate itself is asserted by
    ///     <see cref="RequireIsolation_OnRefusal_ThrowsWithTheReason" />, so both sides are covered
    ///     rather than one being avoided.
    /// </remarks>
    private static BenchmarkSuite Fallback(BenchmarkSuite suite) => Fast(suite).WithRequireIsolation(false);

    /// <summary>
    ///     Under <c>RequireIsolation</c> - still all-or-nothing, unaffected by R5 part 2 below - a
    ///     suite with several un-addressable bodies names every one of them, in the one message the
    ///     thrown exception carries.
    /// </summary>
    /// <remarks>
    ///     A suite is addressed as a set: one worker measures all of it, so one body that cannot cross
    ///     costs every sibling its isolation. Reporting only the first turned that into a sequence of
    ///     re-runs - fix the one you were told about, discover the next - and the set is what the
    ///     reader actually has to act on. <c>RequireIsolation</c> promises every benchmark isolates or
    ///     the run is refused, which is exactly the guarantee demoting only the offenders would break,
    ///     so this path stays all-or-nothing even after R5 part 2.
    /// </remarks>
    [Fact]
    public async Task A_Suite_With_Several_Unaddressable_Bodies_Names_All_Of_Them()
    {
        var first = Stream.Null;
        var second = new StringWriter();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fast(new BenchmarkSuite("many-offenders")
                    .Add("clean", static () => Thread.SpinWait(200))
                    .Add("holds-a-stream", () => first.Length)
                    .Add("holds-a-writer", () => second.ToString().Length))
                .WithRequireIsolation()
                .RunAsync());

        Assert.Contains("holds-a-stream", ex.Message);
        Assert.Contains("holds-a-writer", ex.Message);

        // The sibling that would have isolated on its own is not blamed for the suite it is in.
        Assert.DoesNotContain("'clean'", ex.Message);
    }

    /// <summary>
    ///     R5 part 2: with isolation best-effort rather than required, a suite with several
    ///     un-addressable bodies no longer loses isolation for every sibling - only the offenders are
    ///     demoted, and only they carry the reason as a warning on their own row.
    /// </summary>
    /// <remarks>
    ///     Before this, <c>Fallback</c> (RequireIsolation off) measured the whole suite in this process,
    ///     which is what <see cref="A_Suite_With_Several_Unaddressable_Bodies_Names_All_Of_Them" />
    ///     used to assert here - the two offenders and the clean sibling alike. The guard R5 part 2 was
    ///     blocked on does not apply: none of the three shares a receiver with another, so none of them
    ///     has to be split from anything to be addressed apart from the others.
    /// </remarks>
    [Fact]
    public async Task A_Suite_With_Several_Unaddressable_Bodies_Isolates_The_Rest_And_Demotes_Only_Them()
    {
        var first = Stream.Null;
        var second = new StringWriter();

        var results = await Fallback(new BenchmarkSuite("many-offenders-demoted")
                .Add("clean", static () => Thread.SpinWait(200))
                .Add("holds-a-stream", () => first.Length)
                .Add("holds-a-writer", () => second.ToString().Length))
            .RunAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        var clean = results.Single(r => r.Name == "clean");
        var stream = results.Single(r => r.Name == "holds-a-stream");
        var writer = results.Single(r => r.Name == "holds-a-writer");

        Assert.Equal(IsolationStatus.Isolated, clean.IsolationStatus);

        Assert.Equal(IsolationStatus.InProcessCapturedState, stream.IsolationStatus);
        Assert.Contains(stream.Warnings, w => w.Contains("holds-a-stream"));

        Assert.Equal(IsolationStatus.InProcessCapturedState, writer.IsolationStatus);
        Assert.Contains(writer.Warnings, w => w.Contains("holds-a-writer"));
    }

    /// <summary>
    ///     R5 part 2's own guard: two benchmarks sharing a captured object are demoted <i>together</i>,
    ///     not one kept and the other split off, even though only one of them is ever the one whose
    ///     own address collides.
    /// </summary>
    /// <remarks>
    ///     Cross-receiver sharing is refused outright by design - see
    ///     <c>StateTransfer.TryCaptureField</c>'s "as well as something else in this group" refusal -
    ///     so whichever of "first" and "second" is addressed second is always the one whose attempt
    ///     fails. Demoting only that one would isolate the other with a <i>rebuilt clone</i> of the
    ///     array while the demoted body kept mutating the <i>live</i> one: the two would stop seeing
    ///     each other's writes with nothing in the result saying so - silent divergence, the exact
    ///     failure this guard exists to refuse. Both ending up demoted is what proves the entangled
    ///     receiver was pulled in rather than only the collision's own side.
    /// </remarks>
    [Fact]
    public async Task Two_Benchmarks_Sharing_Captured_State_Are_Demoted_Together_Not_Split()
    {
        // Two lambdas that both capture the same outer local share one display class - one receiver,
        // deduplicated fine by ReceiverTable.TryIndex, no collision at all. Each closing over its own
        // local function's parameter instead forces two separate display classes, each with its own
        // field referring to the one array both point at - the actual shape the guard is about.
        static Func<int> MakeFirst(int[] s) => () => s.Sum();
        static Func<int> MakeSecond(int[] s) => () => s.Length;

        var shared = new[] { 1, 2, 3 };
        var firstBody = MakeFirst(shared);
        var secondBody = MakeSecond(shared);

        Assert.NotSame(firstBody.Target, secondBody.Target);

        var results = await Fallback(new BenchmarkSuite("shared-capture-demoted")
                .Add("alone", static () => Thread.SpinWait(200))
                .Add("first", firstBody)
                .Add("second", secondBody))
            .RunAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        var alone = results.Single(r => r.Name == "alone");
        var first = results.Single(r => r.Name == "first");
        var second = results.Single(r => r.Name == "second");

        // Unrelated to the sharing, so it isolates on its own regardless of how "first" and "second"
        // are resolved.
        Assert.Equal(IsolationStatus.Isolated, alone.IsolationStatus);

        // Both, not just whichever one's own address happened to collide. "second" is the one whose
        // own attempt collided, so its warning is the direct refusal; "first" was already addressed
        // when the collision was found, so its warning is the cascaded one naming what pulled it back
        // out - which is the assertion that matters here, since a synthesized reason for a receiver
        // that never itself failed is the part a naive "just refuse the second one" fix would not say.
        Assert.Equal(IsolationStatus.InProcessCapturedState, first.IsolationStatus);
        Assert.Equal(IsolationStatus.InProcessCapturedState, second.IsolationStatus);
        Assert.Contains(first.Warnings, w => w.Contains("shares captured state") && w.Contains("second"));
        Assert.Contains(second.Warnings, w => w.Contains("second") && w.Contains("as well as something else"));
    }

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
    ///     Under <c>RequireIsolation</c>, a body whose capture cannot be sent names itself in the
    ///     thrown refusal and suggests the plan factory. A suite has several benchmarks and only one
    ///     of them is usually the problem, so a message that does not say which is not actionable.
    /// </summary>
    /// <remarks>
    ///     A capture of ordinary data is sent and the suite stays isolated; what reaches this path is a
    ///     value whose behaviour is not determined by its contents.
    /// </remarks>
    [Fact]
    public async Task InlineSuite_UnsendableCapture_NamesTheBenchmarkAndSuggestsThePlan()
    {
        var stream = Stream.Null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Fast(new BenchmarkSuite("captures")
                    .Add("clean", () => Thread.SpinWait(200))
                    .Add("dirty", () => stream.Length))
                .WithRequireIsolation()
                .RunAsync());

        Assert.Contains("dirty", ex.Message);
        Assert.Contains("captures", ex.Message);
        Assert.Contains("[BenchmarkPlan]", ex.Message);
    }

    /// <summary>
    ///     R5 part 2: with isolation best-effort, the same suite keeps "clean" isolated and demotes
    ///     only "dirty", carrying why on its own row instead of losing both to a fallback that neither
    ///     of them needed.
    /// </summary>
    [Fact]
    public async Task InlineSuite_UnsendableCapture_IsolatesTheCleanSiblingAndDemotesOnlyItself()
    {
        var stream = Stream.Null;

        var results = await Fallback(new BenchmarkSuite("captures-demoted")
                .Add("clean", () => Thread.SpinWait(200))
                .Add("dirty", () => stream.Length))
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        var clean = results.Single(r => r.Name == "clean");
        var dirty = results.Single(r => r.Name == "dirty");

        Assert.Equal(IsolationStatus.Isolated, clean.IsolationStatus);
        Assert.Equal(IsolationStatus.InProcessCapturedState, dirty.IsolationStatus);
        Assert.Contains(dirty.Warnings, w => w.Contains("dirty") && w.Contains("captures"));
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
            results = await Fallback(new BenchmarkSuite("captured-lifecycle")
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
            results = await Fallback(new BenchmarkSuite("opaque")
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
    ///     This is the suite-shaped counterpart to the Single-mode prepared-state path, and the reason it
    ///     matters more here: one worker measures the whole suite, so a single capturing body takes every
    ///     sibling in-process with it. Naming the preparation keeps the entire set isolated.
    /// </remarks>
    [Fact]
    public async Task StatefulSuite_IsIsolated_AndBuildsStateInTheWorker()
    {
        var results = await Fast(BenchmarkSuite.Over("stateful", () => PreparedStateProbe.Build())
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

        var results = await Fast(BenchmarkSuite.Over("per-benchmark", () => new int[64])
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
    ///     A capturing state factory is refused, so the suite falls back rather than preparing state on
    ///     the wrong side of the boundary.
    /// </summary>
    [Fact]
    public async Task StatefulSuite_WithCapturingState_IsIsolated_AndBuildsTheCapturedSize()
    {
        var size = 64;

        var results = await BenchmarkSuite.Over("captured-state", () => new int[size])
            .Add("a", buffer => buffer.Length == size ? 1 : throw new InvalidOperationException("wrong size"))
            .WithIterations(4)
            .WithWarmup(0)
            .WithOpsPerSample(1)
            .RunAsync();

        var result = Assert.Single(results);

        // Errored is the assertion that matters. A worker that had rebuilt the state from a default
        // rather than from the captured 64 would still isolate, still measure, and still report a
        // clean row - which is why the body throws on any length but the one the caller closed over.
        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);
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

        Assert.Contains("isolation is required", ex.Message);
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

    /// <summary>
    ///     W-26: <c>AddInProcess</c> keeps one benchmark in the host while the rest of the suite is
    ///     measured in a worker.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The all-or-nothing behaviour this replaces is the reason it exists: one body holding
    ///         something that cannot cross took every other benchmark in the suite into the host process
    ///         with it, and <c>WithIsolation(false)</c> was the only lever. The price of measuring one
    ///         un-isolatable thing was every comparison it was part of.
    ///     </para>
    ///     <para>
    ///         The suite runs under the default hard-error gate, which is half the assertion: an
    ///         <c>AddInProcess</c> row is a <i>request</i>, so it must not be counted as a refusal.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task AddInProcess_KeepsOnlyThatBenchmarkInTheHost()
    {
        var stream = Stream.Null;

        var results = await Fast(new BenchmarkSuite("split")
                .Add("isolated", () => Thread.SpinWait(200))
                .AddInProcess("host", () => stream.Length)
                .Add("also-isolated", () => Thread.SpinWait(400)))
            .RunAsync();

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));

        Assert.Equal(IsolationStatus.Isolated, results.Single(r => r.Name == "isolated").IsolationStatus);
        Assert.Equal(IsolationStatus.Isolated, results.Single(r => r.Name == "also-isolated").IsolationStatus);
        Assert.Equal(IsolationStatus.InProcessRequested, results.Single(r => r.Name == "host").IsolationStatus);

        // Declaration order survives the two-pass measurement. Appending the host rows would put every
        // AddInProcess row at the bottom of the table regardless of where it was written.
        Assert.Equal(["isolated", "host", "also-isolated"], results.Select(r => r.Name));
    }

    /// <summary>
    ///     A suite made entirely of <c>AddInProcess</c> members is measured here, with nothing refused.
    /// </summary>
    [Fact]
    public async Task AddInProcess_ForEveryBenchmark_MeasuresHereWithoutRefusing()
    {
        var stream = Stream.Null;

        var results = await Fast(new BenchmarkSuite("all-host")
                .AddInProcess("a", () => stream.Length)
                .AddInProcess("b", () => stream.CanRead))
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.InProcessRequested, r.IsolationStatus));
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
