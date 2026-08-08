using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Which benchmark bodies can be addressed across a process boundary, and which are refused.
/// </summary>
/// <remarks>
///     <para>
///         These cases mirror the ones in <c>CapturingBodyAnalyzerTests</c> one for one. NB0014 tells
///         a developer at compile time that a body will not be isolated, and that promise is only
///         worth making if the runtime agrees - a rule that flagged bodies the runtime accepts would
///         teach people to ignore it, and one that stayed silent on bodies the runtime refuses would
///         be worse than absent.
///     </para>
///     <para>
///         Roslyn's lowering is measured here rather than assumed, because two of these cases do not
///         behave the way the obvious reading suggests - see
///         <see cref="A_Body_Capturing_Only_This_Is_Bound_Directly_To_The_Instance" /> and
///         <see cref="A_NonCapturing_Sibling_Of_A_Capturing_Lambda_Is_Still_Addressable" />.
///     </para>
/// </remarks>
public class BodyRefCaptureTests
{
    private static readonly int[] StaticData = [3, 1, 2];

    private readonly int[] _instanceData = [3, 1, 2];

    private static bool CanAddress(Delegate body, out string refusal)
    {
        var addressed = BodyRef.TryCreate(
            body,
            "test",
            out _,
            out var reason,
            arguments: null,
            stateFactory: null,
            new ReceiverTable(MeasurementOptions.DefaultMaxTransferredStateBytes));

        refusal = reason.Message;

        return addressed;
    }

    [Fact]
    public void A_Constant_Body_Is_Addressable()
    {
        Assert.True(CanAddress(() => 43, out _));
    }

    [Fact]
    public void An_Explicitly_Static_Lambda_Is_Addressable()
    {
        Assert.True(CanAddress(static () => 43, out _));
    }

    /// <summary>
    ///     The finding that makes <c>Delegate.Target is null</c> unusable as the capture test. Roslyn
    ///     lowers this to an <i>instance</i> method on a cached singleton, so its target is non-null
    ///     even though it captures nothing.
    /// </summary>
    [Fact]
    public void A_NonCapturing_Lambda_Has_A_Receiver_And_Is_Still_Addressable()
    {
        Func<int> body = static () => 43;

        Assert.NotNull(body.Target);
        Assert.True(CanAddress(body, out _));
    }

    [Fact]
    public void A_Body_Over_Its_Own_Locals_Is_Addressable()
    {
        Assert.True(CanAddress(() =>
        {
            var data = new[] { 3, 1, 2 };
            Array.Sort(data);
            return data[0];
        }, out _));
    }

    [Fact]
    public void A_Body_Over_A_Static_Field_Is_Addressable()
    {
        Assert.True(CanAddress(() => StaticData.Length, out _));
    }

    [Fact]
    public void A_Body_Capturing_A_Local_Is_Addressable_Because_The_Value_Is_Sent()
    {
        var data = new[] { 3, 1, 2 };

        Assert.True(CanAddress(() => data.Length, out var refusal), refusal);
    }

    /// <summary>
    ///     A lambda that captures <i>only</i> <c>this</c> gets no display class at all - Roslyn emits
    ///     it as an ordinary instance method on the containing type, so its receiver is the live
    ///     object itself. It is refused, but as live user state rather than as a closure, which is
    ///     both accurate and a different message than the capture branch produces.
    /// </summary>
    [Fact]
    public void A_Body_Capturing_Only_This_Is_Bound_Directly_To_The_Instance()
    {
        Func<int> body = () => _instanceData.Length;

        Assert.Same(this, body.Target);
        Assert.True(CanAddress(body, out var refusal), refusal);
    }

    /// <summary>
    ///     Mixing <c>this</c> with a local does produce a display class, holding both the captured
    ///     local and a reference to the instance.
    /// </summary>
    [Fact]
    public void A_Body_Capturing_This_And_A_Local_Is_Refused_As_A_Closure()
    {
        var extra = 5;

        Assert.True(CanAddress(() => _instanceData.Length + extra, out var refusal), refusal);
    }

    /// <summary>
    ///     A method group over a live object. The receiver is user state with no cross-process
    ///     meaning, and unlike a closure it is not even compiler-generated.
    /// </summary>
    [Fact]
    public void A_Method_Group_Over_A_Live_Object_Is_Refused()
    {
        var widget = new Widget();

        Assert.True(CanAddress(widget.Compute, out var refusal), refusal);
    }

    [Fact]
    public void A_Method_Group_Over_A_Static_Method_Is_Addressable()
    {
        Assert.True(CanAddress(Widget.ComputeStatic, out _));
    }

    /// <summary>
    ///     Scope merging does <b>not</b> cost a non-capturing lambda its isolation. Roslyn hoists it
    ///     to the shared field-less <c>&lt;&gt;c</c> singleton and gives the capturing sibling its own
    ///     display class, so the two do not share storage and the refusal does not spread.
    /// </summary>
    /// <remarks>
    ///     Worth pinning because the opposite was expected during design: if a non-capturing body
    ///     could be refused for a neighbour's capture, NB0014 would have a false negative by
    ///     construction, since a per-lambda rule cannot see a per-scope property. It cannot.
    /// </remarks>
    [Fact]
    public void A_NonCapturing_Sibling_Of_A_Capturing_Lambda_Is_Still_Addressable()
    {
        var captured = 5;

        Func<int> capturing = () => captured;
        Func<int> selfContained = () => 43;

        Assert.True(CanAddress(capturing, out _));
        Assert.True(CanAddress(selfContained, out _));

        // Kept live so the compiler cannot narrow the scope out from under the premise.
        Assert.Equal(5, capturing());
    }

    /// <summary>
    ///     Where scope merging does show: two lambdas that <i>both</i> capture share one display class
    ///     holding both sets of fields, so each one's refusal names symbols the other captured.
    /// </summary>
    /// <remarks>
    ///     The decision stays correct - both genuinely capture and both are genuinely refused - but
    ///     the message is broader than the body it describes. That is the limitation to state rather
    ///     than to fix: the fields are on one class, and nothing at runtime records which lambda put
    ///     each one there.
    /// </remarks>
    [Fact]
    public void Two_Capturing_Siblings_Share_A_Display_Class_So_Each_Refusal_Names_Both()
    {
        var first = 5;
        var second = 7;

        Func<int> usesFirst = () => first;
        Func<int> usesSecond = () => second;

        Assert.True(CanAddress(usesFirst, out _));
        Assert.True(CanAddress(usesSecond, out _));

        Assert.Equal(5, usesFirst());
        Assert.Equal(7, usesSecond());
    }

    /// <summary>
    ///     The refusal is worth reading, not just acting on: a developer who is told a body cannot be
    ///     isolated needs to know why reconstructing the closure is not on offer.
    /// </summary>
    [Fact]
    public void A_Refusal_Explains_Why_The_Capture_Is_Not_Reconstructed()
    {
        var connection = new NonTransferable();

        Assert.False(CanAddress(() => connection.Use(), out var refusal));

        Assert.Contains("connection", refusal);
        Assert.Contains(nameof(NonTransferable), refusal);
        Assert.Contains("prepare", refusal);
    }

    /// <summary>
    ///     A body wider than <see cref="ArgumentBinder.MaxArity" /> is refused while planning, rather
    ///     than sent and faulted on arrival.
    /// </summary>
    /// <remarks>
    ///     The two sides used to disagree: encoding accepted any arity whose types crossed, and the
    ///     ceiling was enforced only in the worker - so a four-parameter body passed planning, was
    ///     sent, and cost the run a benchmark to a shape the coordinator could have declined and named
    ///     the fix for. Unreachable through the public suite API, which caps parameter sweeps at three,
    ///     which is exactly why it needs pinning here.
    /// </remarks>
    [Fact]
    public void A_Body_Wider_Than_The_Arity_Ceiling_Is_Refused_While_Planning()
    {
        var body = (int a, int b, int c, int d) => a + b + c + d;

        Assert.False(
            BodyRef.TryCreate(body, "test", out _, out var refusal, arguments: [1, 2, 3, 4]));

        Assert.Equal(RefusalReason.UnaddressableArguments, refusal.Reason);
        Assert.Contains("at most 3", refusal.Message);
    }

    /// <summary>
    ///     Argument values and a prepare delegate are two answers for the same parameter slot, so
    ///     supplying both is refused.
    /// </summary>
    /// <remarks>
    ///     Previously the arguments were silently dropped - the encoding branch is skipped whenever a
    ///     factory is present - so the body measured the factory's value under a request that named a
    ///     different one. The "mutually exclusive" claim on <c>BodyRef.StateFactory</c> held by
    ///     construction only, which is an argument about today's callers rather than about the type.
    /// </remarks>
    [Fact]
    public void A_Body_Given_Both_Arguments_And_A_Prepare_Delegate_Is_Refused()
    {
        var body = (int value) => value * 2;

        Assert.False(BodyRef.TryCreate(
            body,
            "test",
            out _,
            out var refusal,
            arguments: [7],
            stateFactory: static () => 3));

        Assert.Equal(RefusalReason.UnaddressableArguments, refusal.Reason);
        Assert.Contains("two different answers", refusal.Message);
    }

    private sealed class Widget
    {
        public int Compute() => 43;

        public static int ComputeStatic() => 43;
    }

    /// <summary>
    ///     Holds something whose behaviour is not determined by its contents, so it is refused and
    ///     the field is named.
    /// </summary>
    private sealed class NonTransferable
    {
        private readonly Stream _handle = Stream.Null;

        public long Use() => _handle.Length;
    }
}
