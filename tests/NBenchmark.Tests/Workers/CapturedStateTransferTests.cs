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
    ///     A7: the element count used to be derived from the byte count via
    ///     <c>Marshal.SizeOf(element)</c> - the unmanaged marshaled size, a different question from
    ///     the managed size <c>Buffer.BlockCopy</c> actually moves. The two happened to agree for
    ///     every element type reaching this path, so the gap was never live - but <c>nint</c> is the
    ///     type whose managed size is architecture-dependent, so it is the one most exposed if that
    ///     ever stopped being true. The count now travels on the wire rather than being derived at
    ///     all; this pins that the array still arrives with the right shape and values.
    /// </summary>
    [Fact]
    public void A_Captured_NativeInt_Array_Arrives_Intact()
    {
        nint[] values = [10, 20, 30, 40, 50];

        var saw = WorkerSaw(() => throw new InvalidOperationException(
            $"len={values.Length},sum={values.Sum(v => (long)v)}"));

        Assert.NotNull(saw);
        Assert.Contains("len=5", saw);
        Assert.Contains("sum=150", saw);
    }

    /// <summary>
    ///     The widest fixed-size element, 8 bytes - byte's own size, the narrowest, is already covered
    ///     by the pre-existing 512-byte payload test below.
    /// </summary>
    [Fact]
    public void A_Captured_Long_Array_Arrives_Intact()
    {
        long[] values = [1_000_000_000_000L, 2_000_000_000_000L];

        var saw = WorkerSaw(() => throw new InvalidOperationException($"sum={values.Sum()}"));

        Assert.NotNull(saw);
        Assert.Contains("sum=3000000000000", saw);
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

        var result = Benchmark.Run(
            () => opaque.Use(), FastOptions with { RequireIsolation = false }, name: "opaque");

        Assert.Equal(IsolationStatus.InProcessCapturedState, result.IsolationStatus);
    }

    /// <summary>
    ///     A dictionary built with a genuinely custom comparer - one that carries configuration of its
    ///     own - is refused. It round-trips into one with identical entries and different lookup cost,
    ///     so no comparison of the data could catch it, and a fresh instance would not carry whatever
    ///     the comparer's own fields hold - which is why the rule inspects the comparer rather than the
    ///     contents. R4 narrows this to comparers that are not reproducible; see
    ///     <see cref="A_Collection_With_A_Named_Framework_Comparer_Isolates" /> for the case it lifted.
    /// </summary>
    [Fact]
    public void A_Collection_With_A_Custom_Stateful_Comparer_Is_Refused()
    {
        var byPrefix = new Dictionary<string, int>(new PrefixComparer(length: 3)) { ["abc"] = 1 };

        Assert.False(
            BodyRef.TryCreate(() => byPrefix.Count, "test", out _, out var refusal, receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("comparer", refusal.Message);
    }

    /// <summary>
    ///     A dictionary built with a named <see cref="StringComparer" /> singleton is no longer refused:
    ///     the comparer's identity travels with the capture, and the worker rebuilds the same static
    ///     instance rather than guessing at an equivalent one from the entries alone.
    /// </summary>
    [Fact]
    public void A_Collection_With_A_Named_Framework_Comparer_Isolates()
    {
        var caseInsensitive = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["a"] = 1 };

        Assert.True(
            BodyRef.TryCreate(() => caseInsensitive.Count, "test", out _, out var refusal, receivers: Table()),
            refusal.Message);
    }

    /// <summary>
    ///     A comparer with no fields of its own cannot have been configured with anything a fresh
    ///     instance would not also have, so it travels by type name rather than being refused - once
    ///     its author has opted in, the same as every other user type.
    /// </summary>
    [Fact]
    public void A_Collection_With_A_Stateless_User_Comparer_Isolates()
    {
        var caseInsensitive = new Dictionary<string, int>(new StatelessCaseInsensitiveComparer()) { ["a"] = 1 };

        Assert.True(
            BodyRef.TryCreate(() => caseInsensitive.Count, "test", out _, out var refusal, receivers: Table()),
            refusal.Message);
    }

    /// <summary>
    ///     Statelessness alone is not enough - nothing is inferred here either. Without
    ///     <c>[BenchmarkState]</c> the comparer is refused exactly as a custom, stateful one is, even
    ///     though a fresh instance would in fact behave identically.
    /// </summary>
    [Fact]
    public void A_Stateless_Comparer_Without_BenchmarkState_Is_Still_Refused()
    {
        var caseInsensitive = new Dictionary<string, int>(new UnmarkedStatelessComparer()) { ["a"] = 1 };

        Assert.False(
            BodyRef.TryCreate(() => caseInsensitive.Count, "test", out _, out var refusal, receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("comparer", refusal.Message);
    }

    /// <summary>
    ///     Not just "it isolated" - the worker's dictionary actually looks keys up case-insensitively,
    ///     which only holds if the comparer that crossed is the real
    ///     <see cref="StringComparer.OrdinalIgnoreCase" /> singleton rather than a guess reconstructed
    ///     from the entries.
    /// </summary>
    [Fact]
    public void A_Named_Framework_Comparer_Rebuilds_With_The_Same_Lookup_Behaviour()
    {
        var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Key"] = 42 };

        var saw = WorkerSaw(() => throw new InvalidOperationException($"found={lookup.ContainsKey("KEY")}"));

        Assert.NotNull(saw);
        Assert.Contains("found=True", saw);
    }

    /// <summary>Same proof for a stateless user comparer, which travels by type name rather than a fixed key.</summary>
    [Fact]
    public void A_Stateless_User_Comparer_Rebuilds_With_The_Same_Lookup_Behaviour()
    {
        var lookup = new HashSet<string>(new StatelessCaseInsensitiveComparer()) { "Key" };

        var saw = WorkerSaw(() => throw new InvalidOperationException($"found={lookup.Contains("KEY")}"));

        Assert.NotNull(saw);
        Assert.Contains("found=True", saw);
    }

    /// <summary>A comparer whose own field means the entries alone can never reproduce it.</summary>
    private sealed class PrefixComparer(int length) : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
            => x is not null && y is not null && Prefix(x) == Prefix(y);

        public int GetHashCode(string obj) => Prefix(obj).GetHashCode();

        private string Prefix(string s) => s.Length <= length ? s : s[..length];
    }

    /// <summary>
    ///     Opted in and provably stateless - a fresh instance is identical to this one - the R4 escape
    ///     hatch for a comparer.
    /// </summary>
    [BenchmarkState]
    private sealed class StatelessCaseInsensitiveComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string obj) => obj.ToUpperInvariant().GetHashCode();
    }

    /// <summary>Same shape as <see cref="StatelessCaseInsensitiveComparer" />, deliberately not opted in.</summary>
    private sealed class UnmarkedStatelessComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string obj) => obj.ToUpperInvariant().GetHashCode();
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
            BodyRef.TryCreate(() => data.Length + alias.Length, "test", out _, out var refusal, receivers: Table()));

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
            recipes: null,
            new ReceiverTable(budgetBytes: 1024)));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("prepare", refusal.Message);
    }

    /// <summary>
    ///     D5: the budget counts what a blittable array actually costs on the wire - base64, always
    ///     larger than the raw bytes - not what it costs in memory. Old accounting compared the raw
    ///     length against the budget, so setting the budget to exactly the raw length must still
    ///     refuse, because base64 is strictly larger for any non-empty array.
    /// </summary>
    [Fact]
    public void The_Budget_Counts_Base64_Encoded_Binary_Bytes_Not_Raw_Ones()
    {
        var raw = new byte[300];

        Assert.False(BodyRef.TryCreate(
            () => raw.Length, "test", out _, out var refusal, receivers: new ReceiverTable(budgetBytes: raw.Length)));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("encoded", refusal.Message);
    }

    /// <summary>The other direction: a budget that genuinely covers the encoded size still admits it.</summary>
    [Fact]
    public void An_Array_Within_The_Encoded_Budget_Still_Isolates()
    {
        var raw = new byte[300];

        Assert.True(
            BodyRef.TryCreate(() => raw.Length, "test", out _, out var refusal, receivers: new ReceiverTable(1024)),
            refusal.Message);
    }

    /// <summary>
    ///     D5's other half: a JSON payload is itself embedded as a JSON <i>string</i> in the frame, so
    ///     it is escaped a second time - every quote and backslash the payload already contains costs
    ///     an extra byte. Old accounting compared the pre-escaping length against the budget, so a
    ///     budget set to exactly that length must still refuse once escaping is counted.
    /// </summary>
    [Fact]
    public void The_Budget_Counts_Escaped_Json_Bytes_Not_Raw_Ones()
    {
        var many = Enumerable.Repeat("a", 2000).ToList();

        var generous = Table();

        Assert.True(BodyRef.TryCreate(() => many.Count, "test", out _, out var setupRefusal, receivers: generous),
            setupRefusal.Message);

        var rawLength = generous.Receivers[0].Captures[0].Json!.Length;

        Assert.False(BodyRef.TryCreate(
            () => many.Count, "test", out _, out var refusal, receivers: new ReceiverTable(budgetBytes: rawLength)));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("encoded", refusal.Message);
    }

    /// <summary>
    ///     Two benchmarks closing over the same state share it in the worker, exactly as they do here.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The regression this pins was measured rather than reasoned about. With a copy of the
    ///         captures on each address, this suite ran isolated and <c>observe</c> saw <c>0</c> where
    ///         the same source in this process showed it a non-zero count - identical code, two
    ///         different programs, decided by whether a worker was available.
    ///     </para>
    ///     <para>
    ///         <c>observe</c> throwing is the evidence: it only throws when it can see what <c>bump</c>
    ///         wrote, which requires both to be bound to one receiver. Declaration order pins which
    ///         runs first.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task Two_Benchmarks_Sharing_One_Capture_Share_It_In_The_Worker()
    {
        var counter = new int[1];

        var results = await new BenchmarkSuite("shared")
            .Add("bump", () => counter[0]++)
            .Add("observe", () =>
            {
                if (counter[0] > 0)
                    throw new InvalidOperationException($"SHARED:{counter[0]}");
            })
            .WithRunOrder(RunOrder.Declaration)
            .WithIterations(4)
            .WithWarmup(0)
            .WithOpsPerSample(1)
            .RunAsync();

        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));

        var observe = results.Single(r => r.Name == "observe");

        Assert.True(observe.Errored, "observe did not see bump's writes, so the receiver was not shared");
        Assert.Contains("SHARED:", observe.ErrorMessage);

        // And the counter in *this* process is untouched: the worker measured its own copy of the
        // shared state, which is the whole point of measuring elsewhere.
        Assert.Equal(0, counter[0]);
    }

    /// <summary>
    ///     A lifecycle hook and the body it belongs to share one receiver, so the hook acts on the
    ///     state the body reads.
    /// </summary>
    /// <remarks>
    ///     Hooks used to refuse captures outright, because addressing them independently would have
    ///     given each a private copy - <c>setup: () =&gt; Array.Clear(buffer)</c> clearing a buffer the
    ///     body never reads is silent and looks like a working benchmark. A shared table is what makes
    ///     them safe to carry.
    /// </remarks>
    [Fact]
    public async Task A_Hook_And_Its_Body_Share_One_Receiver()
    {
        var buffer = new int[1];

        var results = await new BenchmarkSuite("hooked")
            .Add(
                "body",
                () =>
                {
                    if (buffer[0] != 7)
                        throw new InvalidOperationException($"setup did not reach this body: saw {buffer[0]}");
                },
                setup: () => buffer[0] = 7)
            .WithIterations(4)
            .WithWarmup(0)
            .WithOpsPerSample(1)
            .RunAsync();

        var result = Assert.Single(results);

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.Equal(IsolationStatus.Isolated, result.IsolationStatus);

        // Untouched here: the hook ran in the worker, on the worker's copy.
        Assert.Equal(0, buffer[0]);
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

    /// <summary>
    ///     A method group over an <b>inherited</b> method is measured against the object it was taken
    ///     from, not against the class that declared the method.
    /// </summary>
    /// <remarks>
    ///     The receiver's type used to be inferred in the worker from the method's declaring type, on
    ///     the reasoning that a lambda's receiver <i>is</i> its declaring type. True for a lambda and
    ///     false for an inherited method: the coordinator walked the fields of a <c>TurboEngine</c>
    ///     while the worker allocated an <c>Engine</c>. Because the derived class adds no fields, every
    ///     token still resolved and every value still landed - so the run measured the base class's
    ///     override under the derived class's name and said nothing. In-process it answered 4000; in a
    ///     worker, 1000.
    /// </remarks>
    [Fact]
    public void A_Method_Group_Over_An_Inherited_Method_Keeps_The_Derived_Override()
    {
        var saw = WorkerSaw(new TurboEngine().Tick);

        Assert.NotNull(saw);
        Assert.Contains("rpm=4000", saw);
    }

    /// <summary>
    ///     The same substitution one level down: a lambda declared in a base class holds its
    ///     <c>this</c> in a field declared as the base, and the value there can be a subclass.
    /// </summary>
    [Fact]
    public void A_Lambda_Capturing_This_From_A_Derived_Instance_Keeps_The_Override()
    {
        var saw = WorkerSaw(new Triangle().Body(scale: 3));

        Assert.NotNull(saw);
        Assert.Contains("area=9", saw);
    }

    /// <summary>
    ///     Two bodies on one object whose methods are declared at different points in the hierarchy
    ///     bind whichever order they are measured in.
    /// </summary>
    /// <remarks>
    ///     The receiver table entry is shared, so the type it was built as used to be settled by
    ///     whichever body reached it first - and the bodies are shuffled. The suite passed or failed on
    ///     the seed. Both declaration orders are run here because that is the difference the defect
    ///     turned on.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Two_Bodies_On_One_Object_Bind_In_Either_Order(bool inheritedFirst)
    {
        var engine = new TurboEngine();
        var suite = new BenchmarkSuite($"hierarchy-{inheritedFirst}");

        // Method groups, not lambdas: two delegates on one object whose declaring types differ, which
        // is the shape that made the entry's type depend on the run order.
        if (inheritedFirst)
            suite.Add("read", engine.Read).Add("boost", engine.Boost);
        else
            suite.Add("boost", engine.Boost).Add("read", engine.Read);

        var results = await suite
            .WithRunOrder(RunOrder.Declaration)
            .WithIterations(4)
            .WithWarmup(0)
            .WithOpsPerSample(1)
            .RunAsync();

        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));
    }

    /// <summary>
    ///     An array of a base type holding a subclass is refused. Serialization projects each element
    ///     against the <i>element</i> type, so it would arrive as a base instance with the override
    ///     gone - the substitution the field-level check prevents, one level down.
    /// </summary>
    [Fact]
    public void A_Collection_Holding_A_Subclass_Of_Its_Element_Type_Is_Refused()
    {
        Node[] nodes = [new Leaf { Weight = 1, Extra = 2 }];

        Assert.False(
            BodyRef.TryCreate(() => nodes.Length, "test", out _, out var refusal, receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("Leaf", refusal.Message);
        Assert.Contains("Node", refusal.Message);
    }

    /// <summary>An array whose elements really are their declared type still crosses.</summary>
    [Fact]
    public void A_Collection_Of_Its_Own_Element_Type_Is_Faithful()
    {
        Node[] nodes = [new Node { Weight = 1 }, new Node { Weight = 2 }];

        Assert.True(StateTransfer.IsFaithful(typeof(Node[]), nodes, out var why), why);
    }

    /// <summary>
    ///     A field declared as an interface, holding an implementation the rule admits, crosses.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An interface can never be its own runtime type, so the old rule - refuse whenever the
    ///         runtime and declared types differ - fired here before the allow-list was ever consulted.
    ///         <c>IReadOnlyList&lt;&gt;</c> is on that list and was unreachable at the top level: a
    ///         listed entry that nothing could ever satisfy.
    ///     </para>
    ///     <para>
    ///         The hazard the old rule named is still caught, by
    ///         <see cref="An_Interface_Holding_An_Unlistable_Implementation_Is_Refused" /> below. What
    ///         changed is which question gets asked - what the value <i>is</i>, rather than whether it
    ///         matches a declaration.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_Interface_Field_Holding_A_Listed_Implementation_Arrives_Intact()
    {
        IReadOnlyList<int> values = new List<int> { 4, 5, 6 };

        var saw = WorkerSaw(() =>
            throw new InvalidOperationException($"sum={values.Sum()},type={values.GetType().Name}"));

        Assert.NotNull(saw);
        Assert.Contains("sum=15", saw);

        // Rebuilt as what it was, not as the interface's default stand-in.
        Assert.Contains("type=List`1", saw);
    }

    /// <summary>
    ///     The hazard the mismatch rule was written for: an implementation whose behaviour is not
    ///     determined by its contents, behind an interface that would flatten it into one whose is.
    /// </summary>
    [Fact]
    public void An_Interface_Holding_An_Unlistable_Implementation_Is_Refused()
    {
        IReadOnlyList<int> paged = new PagedList(10);

        Assert.False(BodyRef.TryCreate(() => paged.Count, "test", out _, out var refusal, receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains(nameof(PagedList), refusal.Message);
    }

    /// <summary>
    ///     An element type that cannot be subclassed is never enumerated, so the large captures pay
    ///     nothing for the check above.
    /// </summary>
    [Fact]
    public void A_Sealed_Element_Type_Still_Transfers()
    {
        var words = new[] { "a", "b", "c" };

        var saw = WorkerSaw(() => throw new InvalidOperationException($"joined={string.Concat(words)}"));

        Assert.NotNull(saw);
        Assert.Contains("joined=abc", saw);
    }

    /// <summary>
    ///     R3: a rectangular array is admitted alongside the jagged form it was inconsistent with -
    ///     `Buffer.BlockCopy` moves the same bytes either way, so only the shape needed a wire slot.
    /// </summary>
    [Fact]
    public void A_Rectangular_Array_Arrives_Intact()
    {
        var grid = new int[2, 3] { { 1, 2, 3 }, { 4, 5, 6 } };

        var saw = WorkerSaw(() => throw new InvalidOperationException(
            $"dims={grid.GetLength(0)}x{grid.GetLength(1)},corner={grid[1, 2]},sum={grid[0, 0] + grid[1, 2]}"));

        Assert.NotNull(saw);
        Assert.Contains("dims=2x3", saw);
        Assert.Contains("corner=6", saw);
        Assert.Contains("sum=7", saw);
    }

    /// <summary>A three-dimensional array is not a special case - the rank travels either way.</summary>
    [Fact]
    public void A_Three_Dimensional_Array_Arrives_Intact()
    {
        var cube = new int[2, 2, 2];
        cube[1, 1, 1] = 42;

        var saw = WorkerSaw(() => throw new InvalidOperationException($"corner={cube[1, 1, 1]}"));

        Assert.NotNull(saw);
        Assert.Contains("corner=42", saw);
    }

    /// <summary>A rectangular array of a non-blittable element has no wire form and is still refused.</summary>
    [Fact]
    public void A_Rectangular_Array_Of_A_Non_Blittable_Element_Is_Refused()
    {
        var grid = new decimal[2, 2];

        Assert.False(BodyRef.TryCreate(() => grid.Length, "test", out _, out var refusal, receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("multi-dimensional", refusal.Message);
    }

    [Fact]
    public void An_ImmutableArray_Arrives_Intact()
    {
        var values = System.Collections.Immutable.ImmutableArray.Create(1, 2, 3);

        var saw = WorkerSaw(() => throw new InvalidOperationException($"sum={values.Sum()}"));

        Assert.NotNull(saw);
        Assert.Contains("sum=6", saw);
    }

    /// <summary>
    ///     A default, uninitialized ImmutableArray throws on nearly every member - including the one
    ///     the serializer would call - so it is refused structurally rather than by exception.
    /// </summary>
    [Fact]
    public void A_Default_ImmutableArray_Is_Refused()
    {
        var values = default(System.Collections.Immutable.ImmutableArray<int>);

        Assert.False(BodyRef.TryCreate(() => values.IsDefault, "test", out _, out var refusal, receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("default", refusal.Message);
    }

    [Fact]
    public void A_ReadOnlyCollection_Arrives_Intact()
    {
        var values = new System.Collections.ObjectModel.ReadOnlyCollection<int>([1, 2, 3]);

        var saw = WorkerSaw(() => throw new InvalidOperationException($"sum={values.Sum()}"));

        Assert.NotNull(saw);
        Assert.Contains("sum=6", saw);
    }

    [Fact]
    public void An_ArraySegment_Arrives_Intact()
    {
        var values = new ArraySegment<int>(new[] { 0, 1, 2, 3, 4 }, offset: 1, count: 3);

        var saw = WorkerSaw(() => throw new InvalidOperationException($"joined={string.Join(",", values)}"));

        Assert.NotNull(saw);
        Assert.Contains("joined=1,2,3", saw);
    }

    [Fact]
    public void A_Queue_Arrives_Intact_In_Order()
    {
        var values = new Queue<int>([1, 2, 3]);

        var saw = WorkerSaw(() => throw new InvalidOperationException($"dequeued={values.Dequeue()}"));

        Assert.NotNull(saw);
        Assert.Contains("dequeued=1", saw);
    }

    [Fact]
    public void A_LinkedList_Arrives_Intact_In_Order()
    {
        var values = new LinkedList<int>([1, 2, 3]);

        var saw = WorkerSaw(() => throw new InvalidOperationException($"first={values.First!.Value}"));

        Assert.NotNull(saw);
        Assert.Contains("first=1", saw);
    }

    /// <summary>
    ///     The regression a probe caught rather than reasoning: <c>Stack&lt;T&gt;</c> serializes
    ///     top-first, and feeding that straight back into the constructor pops the entries in reverse
    ///     of the stack that was captured. Peek is the assertion that would not have noticed a stack
    ///     with the same <i>contents</i> in the wrong order - only the wrong top would.
    /// </summary>
    [Fact]
    public void A_Stack_Arrives_Intact_With_The_Same_Pop_Order()
    {
        var values = new Stack<int>();
        values.Push(1);
        values.Push(2);
        values.Push(3);

        var saw = WorkerSaw(() => throw new InvalidOperationException($"top={values.Peek()},next={values.ElementAt(1)}"));

        Assert.NotNull(saw);
        Assert.Contains("top=3", saw);
        Assert.Contains("next=2", saw);
    }

    [Fact]
    public void A_ValueTuple_Arrives_Intact()
    {
        var pair = (Count: 5, Label: "widgets");

        var saw = WorkerSaw(() => throw new InvalidOperationException($"n={pair.Count},s={pair.Label}"));

        Assert.NotNull(saw);
        Assert.Contains("n=5", saw);
        Assert.Contains("s=widgets", saw);
    }

    [Fact]
    public void A_Tuple_Arrives_Intact()
    {
        var pair = Tuple.Create(5, "widgets");

        var saw = WorkerSaw(() => throw new InvalidOperationException($"n={pair.Item1},s={pair.Item2}"));

        Assert.NotNull(saw);
        Assert.Contains("n=5", saw);
        Assert.Contains("s=widgets", saw);
    }

    /// <summary>
    ///     R3's codec additions - one representative capture per new scalar, rather than one test each,
    ///     since all five take the same path through <see cref="TestArgumentCodec" />.
    /// </summary>
    [Fact]
    public void The_New_Codec_Scalars_Arrive_Intact()
    {
        var date = new DateOnly(2024, 3, 5);
        var time = new TimeOnly(13, 45, 30);
        var uri = new Uri("https://example.test/path?query=1");
        var version = new Version(1, 2, 3);
        var big = System.Numerics.BigInteger.Parse("123456789012345678901234567890");

        var saw = WorkerSaw(() => throw new InvalidOperationException(
            $"date={date:O},time={time:O},uri={uri},version={version},big={big}"));

        Assert.NotNull(saw);
        Assert.Contains("date=2024-03-05", saw);
        Assert.Contains("time=13:45:30", saw);
        Assert.Contains("uri=https://example.test/path?query=1", saw);
        Assert.Contains("version=1.2.3", saw);
        Assert.Contains("big=123456789012345678901234567890", saw);
    }

    /// <summary>
    ///     <c>[BenchmarkState]</c> no longer waives the comparer rule for what the type holds.
    /// </summary>
    /// <remarks>
    ///     The attribute used to end the walk, so an opted-in type was admitted and nothing inside it
    ///     was looked at. That made it a way to get a dictionary's comparer across unchecked - the crux
    ///     of the whole rule, waived by the escape hatch from it. A stateful comparer still proves the
    ///     rule applies inside an opted-in type; a named framework comparer no longer would, since R4
    ///     made that case reproducible rather than refused - see
    ///     <see cref="A_Collection_With_A_Named_Framework_Comparer_Isolates" />.
    /// </remarks>
    [Fact]
    public void An_Opted_In_Type_Does_Not_Waive_The_Comparer_Rule()
    {
        var index = new Index { Entries = new Dictionary<string, int>(new PrefixComparer(length: 2)) };

        Assert.False(
            BodyRef.TryCreate(() => index.Entries.Count, "test", out _, out var refusal, receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("comparer", refusal.Message);
    }

    /// <summary>
    ///     State the serializer writes but cannot read back is refused rather than delivered as a
    ///     default.
    /// </summary>
    /// <remarks>
    ///     Verified against System.Text.Json rather than reasoned about: a private field never appears
    ///     in the payload at all, while a public readonly field and a get-only property both appear in
    ///     full and are silently discarded on arrival. All three used to be accepted, so an opted-in
    ///     type whose real state was private reached the worker empty and measured a different program.
    /// </remarks>
    [Theory]
    [InlineData(typeof(PrivateState), "private field")]
    [InlineData(typeof(GetOnlyState), "get-only property")]
    [InlineData(typeof(ReadonlyFieldState), "public readonly field")]
    public void State_The_Serializer_Cannot_Restore_Is_Refused(Type stateType, string expected)
    {
        var state = Activator.CreateInstance(stateType, nonPublic: true);

        Assert.False(StateTransfer.IsFaithful(stateType, state, out var why));

        Assert.NotNull(why);
        Assert.Contains(expected, why);
    }

    /// <summary>The documented shape - a record of plain data - still crosses.</summary>
    [Fact]
    public void An_Opted_In_Record_Of_Plain_Data_Is_Still_Faithful()
    {
        var query = new Query("select", 10, ["id", "name"]);

        Assert.True(StateTransfer.IsFaithful(typeof(Query), query, out var why), why);
    }

    /// <summary>
    ///     A capturing lambda declared inside a generic method crosses, with its value intact.
    /// </summary>
    /// <remarks>
    ///     This shape was addressed by the coordinator, sent, and then refused on arrival for the whole
    ///     time the mechanism has existed. The worker resolved the display class's field off the module,
    ///     which yields the <i>open</i> definition's field - whose type is still <c>T</c> and which
    ///     cannot be set on an instance of the closed type - and the repair it reached for,
    ///     <c>GetFieldFromHandle</c>, is rejected by the runtime outright. The branch could only throw,
    ///     and reported it as a bad metadata token. Nothing caught it because the one generic test in
    ///     the suite used a <c>static</c> lambda, which takes the cached-singleton path instead.
    /// </remarks>
    [Fact]
    public void A_Capture_Inside_A_Generic_Method_Arrives_Intact()
    {
        var saw = WorkerSaw(GenericBody(42));

        Assert.NotNull(saw);
        Assert.Contains("seed=42", saw);
    }

    /// <summary>
    ///     A static method on a <b>closed generic type</b> is measured rather than faulting the group.
    /// </summary>
    /// <remarks>
    ///     Nothing closed the declaring type for this shape. Only the two receiver shapes reached the
    ///     type-closing step, and the method-closing step below it handles method generics only, so the
    ///     token resolved to <c>Box&lt;T&gt;.Count</c> and <c>CreateDelegate</c> threw "the containing
    ///     type is not fully instantiated". That is an <see cref="InvalidOperationException" />, which
    ///     the filter around binding did not catch - so instead of one errored row it escaped to the
    ///     group handler and took every remaining benchmark with it.
    /// </remarks>
    [Fact]
    public void A_Static_Method_On_A_Closed_Generic_Type_Is_Measured()
    {
        var saw = WorkerSaw(Box<int>.Report);

        Assert.NotNull(saw);
        Assert.Contains("count=7", saw);
    }

    /// <summary>The same shape one level out: a lambda in a method of a generic class.</summary>
    [Fact]
    public void A_Capture_Inside_A_Generic_Class_Arrives_Intact()
    {
        var saw = WorkerSaw(new Holder<string>().Body("abc"));

        Assert.NotNull(saw);
        Assert.Contains("held=abc", saw);
    }

    /// <summary>
    ///     A private field declared on a base class is restored, not left at its default.
    /// </summary>
    /// <remarks>
    ///     The coordinator walks the hierarchy level by level, because <c>GetFields</c> does not return
    ///     a base type's private fields - so the payload carries a token belonging to whichever level
    ///     declared it. The worker now matches against the same walk rather than resolving the token
    ///     off one module, which is what makes a base declared in another assembly findable at all.
    /// </remarks>
    [Fact]
    public void An_Inherited_Private_Field_Arrives_Intact()
    {
        var saw = WorkerSaw(new AuditedLedger().Report);

        Assert.NotNull(saw);
        Assert.Contains("total=6", saw);
    }

    /// <summary>
    ///     Two different receivers in one group pointing at one array are refused.
    /// </summary>
    /// <remarks>
    ///     The identity set used to be scoped to each receiver while the byte budget was scoped to the
    ///     table, so this case was invisible: the array was sent twice and rebuilt twice, and the two
    ///     benchmarks stopped seeing each other's writes in a worker while sharing one array here. That
    ///     is the exact divergence the receiver table was introduced to end, surviving one level up.
    /// </remarks>
    [Fact]
    public void Two_Receivers_Sharing_One_Object_Are_Refused()
    {
        var shared = new int[4];
        var ledger = new Tally { Entries = shared };
        var table = Table();

        Assert.True(
            BodyRef.TryCreate(ledger.Count, "first", out _, out var first, receivers: table), first.Message);

        Assert.False(
            BodyRef.TryCreate(() => shared.Length, "second", out _, out var refusal, receivers: table));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
        Assert.Contains("this group", refusal.Message);
    }

    /// <summary>
    ///     A prepare delegate that closes over a local is addressed, and its state joins the
    ///     <b>group's</b> table rather than a private one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A factory used to be refused the moment it captured anything, on the reasoning that a
    ///         recipe exists to run in the measuring process and one needing a value from this process
    ///         is not independent of it. The consequence was that the library's own advice failed on a
    ///         shape strictly more transferable than the one it replaced: <c>() =&gt; Sort(Build(size))</c>
    ///         isolated, and <c>prepare: () =&gt; Build(size)</c> - what the refusal message, the
    ///         analyzer and the over-budget message all tell you to write - did not.
    ///     </para>
    ///     <para>
    ///         The table assertion is the half that matters. A recipe handed a table of its own would
    ///         address just as happily and rebuild a <i>second</i> copy of anything the body also
    ///         closed over, so a setup that reset prepared state would reset an array the body never
    ///         reads. One entry here is what says both are bound to one object.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_Capturing_Prepare_Delegate_Joins_The_Groups_Table()
    {
        var size = 16;
        var table = Table();

        Assert.True(
            BodyRef.TryCreate(
                (int[] data) => data.Length,
                "test",
                out var bodyRef,
                out var refusal,
                arguments: null,
                recipes: [StateRecipe.For(() => new int[size])],
                receivers: table),
            refusal.Message);

        Assert.NotNull(bodyRef.Arguments[0].Recipe);

        // One entry, holding the recipe's captured `size`. The body itself closes over nothing.
        var entry = Assert.Single(table.Receivers);
        var captured = Assert.Single(entry.Captures);

        Assert.Equal(nameof(size), captured.FieldName);
    }

    /// <summary>
    ///     A prepare delegate holding something live is still refused, so what opened up is transfer
    ///     and not the rule.
    /// </summary>
    [Fact]
    public void A_Prepare_Delegate_Over_A_Live_Object_Is_Still_Refused()
    {
        var stream = Stream.Null;

        Assert.False(BodyRef.TryCreate(
            (long length) => length,
            "test",
            out _,
            out var refusal,
            arguments: null,
            recipes: [StateRecipe.For(() => stream.Length)],
            receivers: Table()));

        Assert.Equal(RefusalReason.CapturedState, refusal.Reason);
    }

    /// <summary>A table with the default budget, for the addressing-level cases below.</summary>
    private static ReceiverTable Table() => new(MeasurementOptions.DefaultMaxTransferredStateBytes);

    /// <summary>Capturing a value of the method's own type argument, so the display class is generic.</summary>
    private static Action GenericBody<T>(T seed)
    {
        var local = seed;

        return () => throw new InvalidOperationException($"seed={local}");
    }

    /// <summary>
    ///     An <c>IReadOnlyList&lt;int&gt;</c> whose lookup cost is nothing like a list's. Round-tripping
    ///     it through the interface would produce identical entries and a different program, which is
    ///     the substitution the rule exists to catch.
    /// </summary>
    private sealed class PagedList(int pageSize) : IReadOnlyList<int>
    {
        public int Count => pageSize;

        public int this[int index] => index * pageSize;

        public IEnumerator<int> GetEnumerator() => Enumerable.Range(0, Count).GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A static method whose declaring type - not the method - carries the type argument.</summary>
    private static class Box<T>
    {
        public static void Report() => throw new InvalidOperationException($"count={7}");
    }

    private sealed class Holder<T>
    {
        public Action Body(T seed)
        {
            var local = seed;

            return () => throw new InvalidOperationException($"held={local}");
        }
    }

    private class Ledger
    {
        private readonly int[] _entries = [1, 2, 3];

        protected int Total => _entries.Sum();
    }

    private sealed class AuditedLedger : Ledger
    {
        public void Report() => throw new InvalidOperationException($"total={Total}");
    }

    private sealed class Tally
    {
        public int[] Entries = [];

        public int Count() => Entries.Length;
    }

    [BenchmarkState]
    private sealed record Query(string Text, int Limit, string[] Fields);

    private sealed class Opaque
    {
        private readonly Stream _stream = Stream.Null;

        public long Use() => _stream.Length;
    }

    private class Engine
    {
        protected int Rpm = 1000;

        protected virtual int Multiplier => 1;

        /// <summary>Declared here, so a method group over it addresses <c>Engine</c>.</summary>
        public void Tick() => throw new InvalidOperationException($"rpm={Rpm * Multiplier}");

        public int Read() => Rpm;
    }

    private sealed class TurboEngine : Engine
    {
        protected override int Multiplier => 4;

        public int Boost() => Rpm * 2;
    }

    private class Shape
    {
        public virtual int Sides => 0;

        /// <summary>
        ///     Declared on the base and capturing both <c>this</c> and a local, so Roslyn interposes a
        ///     display class whose <c>&lt;&gt;4__this</c> field is typed <c>Shape</c> while the value
        ///     there is whatever subclass built it.
        /// </summary>
        public Action Body(int scale)
        {
            var factor = scale;

            return () => throw new InvalidOperationException($"area={Sides * factor}");
        }
    }

    private sealed class Triangle : Shape
    {
        public override int Sides => 3;
    }

    [BenchmarkState]
    private class Node
    {
        public int Weight { get; set; }
    }

    [BenchmarkState]
    private sealed class Leaf : Node
    {
        public int Extra { get; set; }
    }

    [BenchmarkState]
    private sealed class Index
    {
        public Dictionary<string, int> Entries { get; init; } = [];
    }

    [BenchmarkState]
    private sealed class PrivateState
    {
        private readonly int[] _data = [1, 2, 3];

        public int Length => _data.Length;
    }

    [BenchmarkState]
    private sealed class GetOnlyState
    {
        public int[] Data { get; } = [1, 2, 3];
    }

    [BenchmarkState]
    private sealed class ReadonlyFieldState
    {
        public readonly int[] Data = [1, 2, 3];
    }
}
