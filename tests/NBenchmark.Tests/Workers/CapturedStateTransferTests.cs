using NBenchmark.Attributes;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     A benchmark that closes over a value is measured in a worker, with that value intact.
/// </summary>
/// <remarks>
///     <para>
///         The shape people actually write - <c>var data = Build(); Run(() =&gt; Sum(data));</c> - used
///         to be refused outright, because the only alternative on offer was the probe that fabricated
///         a fresh closure and sent <b>no values</b>: a body over a captured <c>5</c> ran against
///         <c>0</c> and returned a plausible, tight-intervalled result for the wrong number.
///     </para>
///     <para>
///         So the assertion that matters here is not "it was isolated" - that is easy to get wrong in
///         exactly the way the probe did. It is that the worker computed the answer <i>the captured
///         value implies</i>. A body returning a sum is measured for its timing and checked for its
///         result, because a reconstructed-but-empty capture would still produce a clean measurement.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class CapturedStateTransferTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public CapturedStateTransferTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static MeasurementOptions FastOptions => MeasurementOptions.Default with
    {
        Iterations = 4,
        WarmupIterations = 0,
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
    ///     The value the worker saw, recovered by making the benchmark throw it. A body's return value
    ///     never crosses the boundary - only its timing does - so an exception is the only channel a
    ///     measured body has for saying what it observed.
    /// </summary>
    private static string? WorkerSaw(Action body)
    {
        var result = Benchmark.Run(body, FastOptions, name: "probe");

        return result.Errored ? result.ErrorMessage : null;
    }

    [Fact]
    public void A_Captured_Array_Arrives_Intact()
    {
        var data = new[] { 3, 1, 2 };

        var saw = WorkerSaw(() => throw new InvalidOperationException($"sum={data.Sum()},len={data.Length}"));

        Assert.NotNull(saw);
        Assert.Contains("sum=6", saw);
        Assert.Contains("len=3", saw);
    }

    /// <summary>
    ///     The regression the whole mechanism exists to prevent: the probe that was rejected sent no
    ///     values, so a captured <c>5</c> arrived as <c>0</c>.
    /// </summary>
    [Fact]
    public void A_Captured_Scalar_Is_Not_Defaulted()
    {
        var five = 5;
        var label = "hello";

        var saw = WorkerSaw(() => throw new InvalidOperationException($"n={five},s={label}"));

        Assert.NotNull(saw);
        Assert.Contains("n=5", saw);
        Assert.Contains("s=hello", saw);
    }

    [Fact]
    public void A_Captured_Collection_Arrives_Intact()
    {
        var lookup = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var list = new List<string> { "x", "y", "z" };

        var saw = WorkerSaw(() =>
            throw new InvalidOperationException($"map={lookup["a"] + lookup["b"]},list={string.Concat(list)}"));

        Assert.NotNull(saw);
        Assert.Contains("map=3", saw);
        Assert.Contains("list=xyz", saw);
    }

    [Fact]
    public void A_Captured_Byte_Array_Arrives_Intact()
    {
        var payload = new byte[512];

        for (var i = 0; i < payload.Length; i++)
            payload[i] = (byte)(i % 251);

        var saw = WorkerSaw(() =>
            throw new InvalidOperationException($"len={payload.Length},sum={payload.Sum(b => (long)b)}"));

        Assert.NotNull(saw);
        Assert.Contains("len=512", saw);
        Assert.Contains($"sum={payload.Sum(b => (long)b)}", saw);
    }

    /// <summary>
    ///     Captures across nested scopes, which Roslyn links as a chain of display classes.
    /// </summary>
    [Fact]
    public void Captures_Across_Nested_Scopes_Arrive_Intact()
    {
        var outer = 10;
        var saw = (string?)null;

        for (var i = 0; i < 1; i++)
        {
            var inner = 7;

            saw = WorkerSaw(() => throw new InvalidOperationException($"total={outer + inner}"));
        }

        Assert.NotNull(saw);
        Assert.Contains("total=17", saw);
    }

    /// <summary>A user type opted in with <c>[BenchmarkState]</c> crosses by value.</summary>
    [Fact]
    public void An_Opted_In_User_Type_Arrives_Intact()
    {
        var query = new Query("select", 10, ["id", "name"]);

        var saw = WorkerSaw(() =>
            throw new InvalidOperationException($"q={query.Text},n={query.Limit},f={query.Fields.Length}"));

        Assert.NotNull(saw);
        Assert.Contains("q=select", saw);
        Assert.Contains("n=10", saw);
        Assert.Contains("f=2", saw);
    }

    /// <summary>
    ///     A type that has not opted in is refused rather than guessed at, and the message names both
    ///     the field and the remedy.
    /// </summary>
    [Fact]
    public void A_Type_That_Has_Not_Opted_In_Is_Refused()
    {
        var opaque = new Opaque();

        var result = Benchmark.Run(() => opaque.Use(), FastOptions, name: "opaque");

        Assert.Equal(IsolationStatus.InProcessCapturedState, result.IsolationStatus);
    }

    /// <summary>
    ///     A dictionary with a custom comparer is refused. It round-trips into one with identical
    ///     entries and different lookup cost, so no comparison of the data could catch it - which is
    ///     why the rule inspects the comparer.
    /// </summary>
    [Fact]
    public void A_Collection_With_A_Custom_Comparer_Is_Refused()
    {
        var caseInsensitive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 };

        Assert.False(
            BodyRef.TryCreate(() => caseInsensitive.Count, "test", out _, out var refusal));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("comparer", refusal.Message);
    }

    /// <summary>
    ///     Two fields referring to one object are refused: rebuilding them makes two objects where the
    ///     benchmark sees one, and a body that writes through either would observe a different program.
    /// </summary>
    [Fact]
    public void Aliased_Captures_Are_Refused()
    {
        var data = new[] { 1, 2, 3 };
        var alias = data;

        Assert.False(
            BodyRef.TryCreate(() => data.Length + alias.Length, "test", out _, out var refusal));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("same object", refusal.Message);
    }

    /// <summary>
    ///     Past the budget the answer is a prepare delegate, not a truncated value - which would
    ///     measure a smaller input under the caller's name.
    /// </summary>
    [Fact]
    public void A_Capture_Over_The_Budget_Is_Refused()
    {
        var big = new byte[4096];

        Assert.False(BodyRef.TryCreate(
            () => big.Length,
            "test",
            out _,
            out var refusal,
            arguments: null,
            stateFactory: null,
            maxTransferredStateBytes: 1024));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("prepare", refusal.Message);
    }

    /// <summary>
    ///     Two benchmarks closing over the same state are refused, because one worker cannot be given
    ///     that state twice without them observing different copies of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Measured, not reasoned about: with the transfer in place and no guard, this suite ran
    ///         isolated and the second body saw <c>0</c> where the same source in this process showed
    ///         it <c>4</c>. Identical code, two different programs, decided by whether a worker was
    ///         available - which is the exact failure the addressing rules exist to prevent.
    ///     </para>
    ///     <para>
    ///         Refusing is the interim answer. Sending one receiver that the whole group shares is the
    ///         real one, and it is also what would let a lifecycle hook carry captures.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Two_Benchmarks_Sharing_One_Capture_Are_Refused()
    {
        var counter = new int[1];

        var results = await new BenchmarkSuite("shared")
            .Add("bump", () => counter[0]++)
            .Add("observe", () => _ = counter[0])
            .WithIterations(4)
            .WithWarmup(0)
            .WithOpsPerSample(1)
            .RunAsync();

        Assert.All(results, r => Assert.Equal(IsolationStatus.InProcessCapturedState, r.IsolationStatus));
    }

    /// <summary>
    ///     Distinct captures still cross. The guard is about <i>sharing</i>, not about a suite having
    ///     captures at all.
    /// </summary>
    [Fact]
    public async Task Benchmarks_With_Separate_Captures_Still_Isolate()
    {
        var results = await SuiteWithSeparateCaptures();

        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));
    }

    /// <summary>
    ///     Each body's capture is hoisted into its own scope, so the two do not share a display class.
    /// </summary>
    private static Task<IReadOnlyList<BenchmarkResult>> SuiteWithSeparateCaptures()
    {
        var suite = new BenchmarkSuite("separate");

        {
            var first = new[] { 1, 2, 3 };

            suite.Add("a", () => _ = first.Length);
        }

        {
            var second = new[] { 4, 5 };

            suite.Add("b", () => _ = second.Length);
        }

        return suite
            .WithIterations(4)
            .WithWarmup(0)
            .WithOpsPerSample(1)
            .RunAsync();
    }

    [BenchmarkState]
    private sealed record Query(string Text, int Limit, string[] Fields);

    private sealed class Opaque
    {
        private readonly Stream _stream = Stream.Null;

        public long Use() => _stream.Length;
    }
}
