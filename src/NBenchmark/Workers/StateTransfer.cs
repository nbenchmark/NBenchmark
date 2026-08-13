using System.Collections;
using System.Reflection;
using NBenchmark.Attributes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NBenchmark.Workers;

/// <summary>How one captured field's value travels.</summary>
internal enum CapturedValueKind
{
    /// <summary>A JSON projection, for everything the faithfulness rule admits by value.</summary>
    Json = 0,

    /// <summary>
    ///     Raw little-endian bytes, for an array of a blittable primitive.
    /// </summary>
    /// <remarks>
    ///     Not an optimisation afterthought. A <c>byte[]</c> or <c>int[]</c> written as a JSON number
    ///     array is roughly four times its own size and slow to parse, and the frame ceiling is 64 MiB -
    ///     so the JSON form would refuse a capture at a quarter of the size this one carries.
    /// </remarks>
    Binary = 1,

    /// <summary>
    ///     A chained compiler-generated scope, transferred field by field rather than as a value.
    /// </summary>
    /// <remarks>
    ///     Roslyn links display classes when a lambda captures across nested scopes: the inner class
    ///     holds a field pointing at the outer one. Serializing that link as JSON would produce an
    ///     object of the wrong type on the far side, where the worker needs the real display class so
    ///     the compiled method can be bound to it.
    /// </remarks>
    Nested = 2,
}

/// <summary>One captured field's value, addressed by the field's own metadata token.</summary>
internal sealed record CapturedField
{
    /// <summary>
    ///     Metadata token of the field on the receiver's type.
    /// </summary>
    /// <remarks>
    ///     Trusted because the module version id gate has already proved the defining module is the
    ///     same build, which makes the token exact - where a name could bind a different field after a
    ///     rename that happened to keep the shape.
    /// </remarks>
    public required int FieldToken { get; init; }

    /// <summary>The field's name, for diagnostics only. Nothing resolves by it.</summary>
    public required string FieldName { get; init; }

    public required CapturedValueKind Kind { get; init; }

    /// <summary>
    ///     Assembly-qualified name of the field's <b>declared</b> type, re-checked in the worker
    ///     against the field the token resolved to.
    /// </summary>
    public required string DeclaredTypeName { get; init; }

    /// <summary>
    ///     Assembly-qualified name of the value's <b>runtime</b> type. Carried only for
    ///     <see cref="CapturedValueKind.Nested" />, and required there.
    /// </summary>
    /// <remarks>
    ///     A nested scope is rebuilt rather than deserialized, so the worker has to allocate a type -
    ///     and the declared one is the wrong answer. A lambda written in a base class and registered
    ///     from a derived instance holds its <c>&lt;&gt;4__this</c> in a field declared as the base,
    ///     while the walk on this side reads the fields of the derived object it actually points at.
    ///     Rebuilding the declared type would restore those fields onto an instance of the wrong class
    ///     and dispatch every virtual member to the wrong override.
    ///     <para>
    ///         Not carried for the other kinds because they do not need it: a value whose runtime type
    ///         differs from its declared type is refused outright by <see cref="IsFaithful" />, so for
    ///         those two the declared type <i>is</i> the runtime type.
    ///     </para>
    /// </remarks>
    public string? RuntimeTypeName { get; init; }

    public string? Json { get; init; }

    public byte[]? Binary { get; init; }

    public IReadOnlyList<CapturedField>? Nested { get; init; }
}

/// <summary>
///     One receiver a group's delegates bind to, with the values it holds.
/// </summary>
internal sealed record TransferredReceiver
{
    /// <summary>
    ///     Assembly-qualified name of the receiver's <b>runtime</b> type - the type whose fields
    ///     <see cref="Captures" /> describes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This used to be omitted, on the reasoning that every delegate sharing a receiver shares
    ///         its runtime type by construction, so the worker could take the type from whichever
    ///         delegate reached the entry first. The premise is true and the conclusion did not follow:
    ///         what the worker actually had to hand was the method's <i>declaring</i> type, and those
    ///         are the same thing only when the method is declared on the object's own class.
    ///     </para>
    ///     <para>
    ///         For a method group over an inherited method - <c>turbo.Tick</c> where <c>Tick</c> is
    ///         declared on the base - the walk on this side reads the fields of the derived object
    ///         while the worker allocated the base. When the derived class adds no fields of its own
    ///         every token still resolves, every value still lands, and the benchmark measures the base
    ///         class's overrides under the derived class's name. Naming the type is what closes that,
    ///         and it also settles the entry's type before any delegate arrives rather than leaving it
    ///         to the run order.
    ///     </para>
    /// </remarks>
    public required string TypeName { get; init; }

    public required IReadOnlyList<CapturedField> Captures { get; init; }
}

/// <summary>
///     The distinct receivers a group's delegates close over, deduplicated by identity.
/// </summary>
/// <remarks>
///     <para>
///         Receivers belong to the <b>group</b> rather than to each delegate, and that is the whole
///         point. Roslyn merges the captures of every lambda in a lexical scope into one display class,
///         so a suite's bodies and its lifecycle hooks routinely close over one object - and giving
///         each address its own copy of that object's fields meant the worker rebuilt several where
///         this process has one. Measured: <c>.Add("bump", () =&gt; counter[0]++)</c> beside
///         <c>.Add("observe", () =&gt; counter[0])</c> showed <c>observe</c> a <c>4</c> in-process and a
///         <c>0</c> in a worker. Identical source, two different programs.
///     </para>
///     <para>
///         Holding a table means the identity that exists here is reproduced there: one entry per
///         distinct receiver, one rehydration per entry, every delegate that shared an object still
///         sharing it. It is also what lets a lifecycle hook carry captures at all - a hook exists to
///         act on the body's state, so a private copy would have it clearing a buffer the body never
///         reads.
///     </para>
///     <para>
///         The budget is the table's, not each delegate's, because the wire cost is the table's - and
///         so is the identity set, for the same reason one level up. Two <i>different</i> receivers in
///         one group can point at one array, and a set scoped to each of them saw nothing wrong: the
///         array was sent twice and rebuilt twice, so the two benchmarks stopped seeing each other's
///         writes exactly as they did before this table existed. Aliasing is a fact about the group.
///     </para>
/// </remarks>
internal sealed class ReceiverTable(int budgetBytes)
{
    private readonly Dictionary<object, int> _indices = new(ReferenceEqualityComparer.Instance);

    private readonly List<TransferredReceiver> _receivers = [];

    /// <summary>
    ///     Every object already committed to this group's wire form, so a second reference to one from
    ///     anywhere in the group is refused rather than duplicated.
    /// </summary>
    private readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);

    private int _spent;

    /// <summary>The entries built so far, in index order.</summary>
    public IReadOnlyList<TransferredReceiver> Receivers => _receivers;

    /// <summary>
    ///     Returns the index for <paramref name="receiver" />, capturing its fields the first time it
    ///     is seen and reusing the entry every time after.
    /// </summary>
    public bool TryIndex(object receiver, string subject, out int index, out Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        refusal = Refusal.None;

        if (_indices.TryGetValue(receiver, out index))
            return true;

        // The receiver joins the group's identity set before its own walk, so a later receiver holding
        // a reference to *it* is caught as sharing rather than rebuilt as a second object.
        _seen.Add(receiver);

        if (!StateTransfer.TryCapture(receiver, subject, budgetBytes, _seen, ref _spent, out var captured, out refusal))
        {
            index = -1;

            return false;
        }

        index = _receivers.Count;

        // The runtime type, matching the type StateTransfer walked the fields of. Taking it from the
        // object rather than from any delegate bound to it is the whole point - see TypeName.
        _receivers.Add(new TransferredReceiver
        {
            TypeName = StateTransfer.NameOf(receiver.GetType()),
            Captures = captured,
        });

        _indices[receiver] = index;

        return true;
    }
}

/// <summary>
///     Decides whether the values a delegate closes over can be sent to another process, and encodes
///     them when they can.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is not the mechanism that was probed and rejected.</b> That probe fabricated a
///         fresh closure instance and invoked anyway, sending <i>no values at all</i> - so a body over
///         a captured <c>5</c> ran against <c>0</c> and returned a plausible, tight-intervalled
///         result for the wrong number. The values are what makes this different, and the doctrine
///         that produced the original refusal is preserved intact: anything whose behaviour is not
///         determined by the bytes we send is still refused, by name, with the field that caused it.
///     </para>
///     <para>
///         <b>Faithful</b> is a stronger claim than "round-trips". A
///         <c>Dictionary&lt;string,int&gt;(StringComparer.OrdinalIgnoreCase)</c> round-trips into a
///         dictionary with identical contents and different lookup performance, and no comparison of
///         the data could ever catch it - which is why the rule inspects the comparer rather than the
///         entries. The set below is closed for that reason, not for lack of ambition: it is the set
///         whose observable behaviour under measurement is fully determined by its contents.
///     </para>
///     <para>
///         A user type joins the set by carrying <see cref="BenchmarkStateAttribute" />, which is the
///         author asserting exactly that claim about their own type. Nothing infers it.
///     </para>
/// </remarks>
internal static class StateTransfer
{
    /// <summary>
    ///     Serializer settings for the JSON form. Deliberately not <c>FrameChannel</c>'s: these
    ///     payloads are user data rather than protocol records, and the two should not share a policy
    ///     that a change on either side could silently alter for the other.
    /// </summary>
    /// <summary>
    ///     How many scopes deep the walk will go before refusing.
    /// </summary>
    /// <remarks>
    ///     A display-class chain is short by construction - it is bounded by the lexical nesting the
    ///     user wrote. The captured-<c>this</c> branch is not: it follows a <i>user</i> object, whose
    ///     graph can be any depth and can revisit types without ever revisiting an instance, so the
    ///     identity check alone does not terminate it. Without this the walk overflowed the stack, which
    ///     is a crash rather than a refusal - the one failure mode worse than declining.
    /// </remarks>
    private const int MaxScopeDepth = 16;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // A cycle would otherwise be a stack overflow inside the serializer. Bounded here so it
        // surfaces as a refusal naming the field.
        MaxDepth = 32,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        IncludeFields = true,
    };

    /// <summary>
    ///     Captures every instance field of <paramref name="receiver" />, or refuses and names the
    ///     field that could not cross.
    /// </summary>
    /// <param name="subject">
    ///     How to open a refusal sentence about this receiver - "it captures" for a lambda's display
    ///     class, "'Widget' holds" for a user object a body was bound to. Carried rather than derived
    ///     because the two read completely differently to whoever has to act on the message, and a
    ///     reader who wrote <c>widget.Compute</c> is not looking for the word "captures".
    /// </param>
    /// <param name="budgetBytes">
    ///     Ceiling on the encoded size of the whole capture set. A capture larger than this is refused
    ///     with a pointer at the prepare delegate, because at that size building the value in the
    ///     worker is both faster and more faithful than shipping it.
    /// </param>
    /// <param name="seen">
    ///     Every object the <b>group</b> has already committed to sending. Identity is tracked across
    ///     the whole group rather than per field or per receiver: two fields pointing at one array are
    ///     observably shared - a body that mutates through one sees it through the other - and
    ///     rebuilding them as two arrays would measure a program the user did not write. That is as
    ///     true of two fields on two different receivers as it is of two fields on one.
    /// </param>
    public static bool TryCapture(
        object receiver,
        string subject,
        int budgetBytes,
        HashSet<object> seen,
        ref int spent,
        out IReadOnlyList<CapturedField> captured,
        out Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(seen);

        captured = [];
        refusal = Refusal.None;

        if (!TryCaptureInto(receiver, subject, seen, budgetBytes, depth: 0, ref spent, out captured, out refusal))
        {
            captured = [];

            return false;
        }

        return true;
    }

    private static bool TryCaptureInto(
        object receiver,
        string subject,
        HashSet<object> seen,
        int budgetBytes,
        int depth,
        ref int total,
        out IReadOnlyList<CapturedField> captured,
        out Refusal refusal)
    {
        captured = [];
        refusal = Refusal.None;

        if (depth > MaxScopeDepth)
        {
            refusal = new Refusal(
                RefusalReason.CapturedState,
                $"{subject} an object graph more than {MaxScopeDepth} levels deep, which is not walked "
                + "to the end. Name the preparation instead so the worker builds it.");

            return false;
        }

        var fields = InstanceFieldsOf(receiver.GetType());
        var results = new List<CapturedField>(fields.Length);

        foreach (var field in fields)
        {
            var value = field.GetValue(receiver);

            if (!TryCaptureField(
                    field, value, subject, seen, budgetBytes, depth, ref total, out var encoded, out refusal))
                return false;

            results.Add(encoded!);
        }

        captured = results;

        return true;
    }

    private static bool TryCaptureField(
        FieldInfo field,
        object? value,
        string subject,
        HashSet<object> seen,
        int budgetBytes,
        int depth,
        ref int total,
        out CapturedField? captured,
        out Refusal refusal)
    {
        captured = null;
        refusal = Refusal.None;

        var declared = field.FieldType;

        // A chained scope, or the enclosing instance a lambda captured `this` from. Both are recursed
        // rather than serialized: the worker needs an object of the real type, where a JSON projection
        // would give it something of the wrong type holding the right values.
        //
        // Including captured `this` here is what keeps two spellings of one thing consistent. A body
        // capturing only `this` is bound straight to the instance and transferred as a receiver; add a
        // local and Roslyn interposes a display class holding `this` in a field. Treating the second as
        // an ordinary value would refuse a body that the first form isolates, for a difference the
        // user did not write and cannot see.
        if (IsCompilerGeneratedScope(declared) || IsCapturedEnclosingInstance(field))
        {
            if (value is null)
            {
                refusal = new Refusal(
                    RefusalReason.CapturedState,
                    $"{subject} an enclosing scope that was null when the benchmark was registered.");

                return false;
            }

            if (!seen.Add(value))
            {
                refusal = new Refusal(
                    RefusalReason.CapturedState,
                    $"{subject} enclosing scopes that form a cycle or share an instance.");

                return false;
            }

            if (!TryCaptureInto(
                    value, subject, seen, budgetBytes, depth + 1, ref total, out var nested, out refusal))
            {
                return false;
            }

            captured = new CapturedField
            {
                FieldToken = field.MetadataToken,
                FieldName = field.Name,
                Kind = CapturedValueKind.Nested,
                DeclaredTypeName = NameOf(declared),

                // The type the fields above were read from, which for a captured `this` can be a
                // subclass of the field's declared type. See CapturedField.RuntimeTypeName.
                RuntimeTypeName = NameOf(value.GetType()),
                Nested = nested,
            };

            return true;
        }

        if (!IsFaithful(declared, value, out var why))
        {
            refusal = new Refusal(
                RefusalReason.CapturedState,
                $"{subject} '{FriendlyFieldName(field)}' of type '{FriendlyTypeName(declared)}', which "
                + $"cannot be sent to another process: {why}");

            return false;
        }

        // Sharing is only observable through a mutable reference. Strings are immutable and boxed
        // value types are copied on the way in, so neither can be aliased by the body.
        if (value is not null and not string && !declared.IsValueType && !seen.Add(value))
        {
            refusal = new Refusal(
                RefusalReason.CapturedState,
                $"{subject} '{FriendlyFieldName(field)}' as well as something else in this group "
                + "referring to the same object. Rebuilding them would produce two objects where the "
                + "benchmark sees one, so the sharing has to be reproduced by a prepare delegate "
                + "rather than sent.");

            return false;
        }

        if (!TryEncode(field, subject, declared, value, out captured, out refusal))
            return false;

        total += captured!.Binary?.Length ?? captured.Json?.Length ?? 0;

        if (total > budgetBytes)
        {
            refusal = new Refusal(
                RefusalReason.CapturedState,
                $"{subject} state exceeding {budgetBytes / 1024 / 1024} MiB once encoded. At that "
                + "size, building the value in the measuring process is both faster and more faithful "
                + "than sending it - name the preparation with "
                + "Benchmark.Run(prepare: () => Build(), body: d => Use(d)), or .WithState(() => Build()).");

            return false;
        }

        return true;
    }

    private static bool TryEncode(
        FieldInfo field,
        string subject,
        Type declared,
        object? value,
        out CapturedField? captured,
        out Refusal refusal)
    {
        captured = null;
        refusal = Refusal.None;

        var common = new
        {
            Token = field.MetadataToken,
            Name = field.Name,
            TypeName = NameOf(declared),
        };

        if (value is not null && IsBlittablePrimitiveArray(declared))
        {
            captured = new CapturedField
            {
                FieldToken = common.Token,
                FieldName = common.Name,
                Kind = CapturedValueKind.Binary,
                DeclaredTypeName = common.TypeName,
                Binary = ToBytes((Array)value),
            };

            return true;
        }

        try
        {
            captured = new CapturedField
            {
                FieldToken = common.Token,
                FieldName = common.Name,
                Kind = CapturedValueKind.Json,
                DeclaredTypeName = common.TypeName,
                Json = JsonSerializer.Serialize(value, declared, SerializerOptions),
            };

            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            refusal = new Refusal(
                RefusalReason.CapturedState,
                $"{subject} '{FriendlyFieldName(field)}', which could not be encoded: {ex.Message}");

            return false;
        }
    }

    /// <summary>
    ///     Whether a value's observable behaviour under measurement is fully determined by the bytes
    ///     that would be sent for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Note the runtime-type check. A field declared <c>IList&lt;int&gt;</c> holding a
    ///         <c>MyPagedList</c> would round-trip into a <c>List&lt;int&gt;</c> with the same entries
    ///         and entirely different performance, so a value whose runtime type differs from its
    ///         declared type is refused rather than flattened.
    ///     </para>
    ///     <para>
    ///         That check runs at <b>every</b> position the walk reaches, not only at the field itself.
    ///         Applying it to the field alone left the substitution it exists to prevent reachable one
    ///         level down, because a collection is serialized against its <i>element</i> type: a
    ///         <c>Shape[]</c> holding a <c>Square</c> wrote <c>{"Sides":4}</c>, arrived as a
    ///         <c>Shape</c>, and measured the base class's overrides under the derived class's name.
    ///         Elements are only enumerated where a mismatch is possible - a sealed or value element
    ///         type cannot vary - so an <c>int[]</c> or a <c>string[]</c> pays nothing for it.
    ///     </para>
    /// </remarks>
    public static bool IsFaithful(Type declared, object? value, out string? why)
    {
        ArgumentNullException.ThrowIfNull(declared);

        return IsFaithfulValue(declared, value, "it", new FaithfulnessWalk(), out why);
    }

    /// <summary>
    ///     One position - a field, an element, a member - where a declared type has to describe
    ///     whatever value is actually sitting there.
    /// </summary>
    /// <param name="what">
    ///     How to name the position in a refusal. The caller owns the wording because the same failure
    ///     reads completely differently for a captured local, an array element and a member of an
    ///     opted-in type, and the reader has to know which one to go and look at.
    /// </param>
    private static bool IsFaithfulValue(
        Type declared,
        object? value,
        string what,
        FaithfulnessWalk walk,
        out string? why)
    {
        why = null;

        if (value is null)
        {
            if (declared.IsValueType && Nullable.GetUnderlyingType(declared) is null)
            {
                why = $"{what} is null and its declared type is not nullable.";

                return false;
            }

            return true;
        }

        var runtime = value.GetType();

        if (runtime != declared && !declared.IsValueType)
        {
            why = $"{what} holds a '{FriendlyTypeName(runtime)}' where a '{FriendlyTypeName(declared)}' "
                  + "was declared, and rebuilding it would substitute the declared type for the real one.";

            return false;
        }

        return IsFaithfulType(declared, value, walk, out why);
    }

    private static bool IsFaithfulType(Type type, object? value, FaithfulnessWalk walk, out string? why)
    {
        why = null;

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (TestArgumentCodec.IsSupported(underlying))
            return true;

        if (underlying.IsDefined(typeof(BenchmarkStateAttribute), inherit: false))
            return IsFaithfulState(underlying, value, walk, out why);

        if (underlying.IsArray)
        {
            if (underlying.GetArrayRank() != 1)
            {
                why = "multi-dimensional arrays are not transferred.";

                return false;
            }

            var element = underlying.GetElementType()!;

            return IsFaithfulType(element, value: null, walk, out why)
                   && AllItemsFaithful(element, value as IEnumerable, walk, out why);
        }

        if (underlying.IsGenericType)
        {
            var definition = underlying.GetGenericTypeDefinition();
            var arguments = underlying.GetGenericArguments();

            if (definition == typeof(List<>) || definition == typeof(IReadOnlyList<>))
            {
                return AllFaithful(arguments, walk, out why)
                       && AllItemsFaithful(arguments[0], value as IEnumerable, walk, out why);
            }

            if (definition == typeof(KeyValuePair<,>))
                return AllFaithful(arguments, walk, out why) && PairFaithful(underlying, value, walk, out why);

            if (definition == typeof(Memory<>) || definition == typeof(ReadOnlyMemory<>))
            {
                return AllFaithful(arguments, walk, out why)
                       && AllItemsFaithful(arguments[0], BufferOf(underlying, arguments[0], value), walk, out why);
            }

            if (definition == typeof(Dictionary<,>))
            {
                return AllFaithful(arguments, walk, out why)
                       && HasDefaultComparer(underlying, value, typeof(EqualityComparer<>), arguments[0], out why)
                       && AllEntriesFaithful(arguments[0], arguments[1], value, walk, out why);
            }

            if (definition == typeof(HashSet<>))
            {
                return AllFaithful(arguments, walk, out why)
                       && HasDefaultComparer(underlying, value, typeof(EqualityComparer<>), arguments[0], out why)
                       && AllItemsFaithful(arguments[0], value as IEnumerable, walk, out why);
            }

            if (definition == typeof(SortedDictionary<,>))
            {
                return AllFaithful(arguments, walk, out why)
                       && HasDefaultComparer(underlying, value, typeof(Comparer<>), arguments[0], out why)
                       && AllEntriesFaithful(arguments[0], arguments[1], value, walk, out why);
            }

            if (definition == typeof(SortedSet<>))
            {
                return AllFaithful(arguments, walk, out why)
                       && HasDefaultComparer(underlying, value, typeof(Comparer<>), arguments[0], out why)
                       && AllItemsFaithful(arguments[0], value as IEnumerable, walk, out why);
            }
        }

        why = $"'{FriendlyTypeName(type)}' is not one of the types whose measured behaviour is fully "
              + "determined by its contents. Mark it [BenchmarkState] if it is, or build it in the "
              + "measuring process with a prepare delegate.";

        return false;
    }

    /// <summary>
    ///     Walks the members of a type whose author opted in with
    ///     <see cref="BenchmarkStateAttribute" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The attribute used to end the walk: the type was admitted and nothing inside it was
    ///         looked at. That made it a wider claim than its own documentation describes, because it
    ///         silently waived the rules the rest of this class exists to enforce - an opted-in type
    ///         holding a <c>Dictionary</c> built with <c>StringComparer.OrdinalIgnoreCase</c> never
    ///         reached <see cref="HasDefaultComparer" /> at all, and arrived with a different lookup
    ///         cost under the same name. The comparer is the crux of the whole rule; an escape hatch
    ///         that waives it is not an escape hatch, it is a hole.
    ///     </para>
    ///     <para>
    ///         So the attribute now claims what it reads as - <i>this type is transferable</i> - and
    ///         every member is held to the ordinary rule. What it still cannot check, and what it is
    ///         really for, is whether a member's value means something outside its own contents.
    ///     </para>
    ///     <para>
    ///         A member the serializer cannot restore is refused rather than accepted, because the
    ///         alternative is the quietest failure available. The payload is written by
    ///         System.Text.Json, which restores public fields and properties with a setter and nothing
    ///         else - but it <i>writes</i> more than it can read back. A public readonly field and a
    ///         get-only property both appear in the JSON, in full, and are both silently discarded on
    ///         arrival; a private field carrying the type's real state never appears at all. Each one
    ///         reaches the worker as its default, which is precisely the fabricated-closure failure
    ///         this mechanism was built to end.
    ///     </para>
    /// </remarks>
    private static bool IsFaithfulState(Type type, object? value, FaithfulnessWalk walk, out string? why)
    {
        why = null;

        if (!walk.TryDescend(type, out why))
            return false;

        var tracked = value is not null && !type.IsValueType;

        if (tracked && !walk.Visiting.Add(value!))
        {
            why = $"'{FriendlyTypeName(type)}' refers back to itself, and a graph with a cycle in it "
                  + "cannot be rebuilt as one graph in another process.";

            return false;
        }

        try
        {
            foreach (var field in InstanceFieldsOf(type))
            {
                if (SerializerCannotRestore(field, type) is { } lost)
                {
                    why = $"'{FriendlyTypeName(type)}' carries {lost}, which is written to the payload "
                          + "but cannot be restored from it, so it would arrive at its default. Make it "
                          + "a public field or a property with a setter, or build the value in the "
                          + "measuring process with a prepare delegate.";

                    return false;
                }

                var position = $"its member '{MemberNameOf(field, type)}'";

                // With no instance in hand there is nothing to inspect, so the member is checked for
                // the type it declares alone. Reading the absent value as a null would refuse every
                // non-nullable member of a type reached as an element type rather than as a value.
                var faithful = value is null
                    ? IsFaithfulType(field.FieldType, null, walk, out why)
                    : IsFaithfulValue(field.FieldType, field.GetValue(value), position, walk, out why);

                if (faithful)
                    continue;

                // The inner rule names the position for the failures that have one; the type-shaped
                // ones name only a type, and a reader given 'Stream is not transferable' still has to
                // find which member that was.
                if (why is not null && !why.StartsWith(position, StringComparison.Ordinal))
                    why = $"{position} is not transferable: {why}";

                return false;
            }

            return true;
        }
        finally
        {
            if (tracked)
                walk.Visiting.Remove(value!);

            walk.Ascend();
        }
    }

    /// <summary>
    ///     Checks the actual elements of a collection, when the element type is one a subclass could
    ///     be substituted for.
    /// </summary>
    /// <remarks>
    ///     The <see cref="CanVaryAtRuntime" /> guard is what keeps this affordable. An <c>int[]</c>, a
    ///     <c>string[]</c> or a <c>List&lt;Guid&gt;</c> cannot hold anything but what it declares, so
    ///     the walk stops before enumerating a single element - which matters, because these are the
    ///     large captures.
    /// </remarks>
    private static bool AllItemsFaithful(
        Type declaredItem,
        IEnumerable? items,
        FaithfulnessWalk walk,
        out string? why)
    {
        why = null;

        if (items is null || !CanVaryAtRuntime(declaredItem))
            return true;

        foreach (var item in items)
        {
            if (!walk.TryInspect(out why))
                return false;

            if (!IsFaithfulValue(declaredItem, item, "one of its elements", walk, out why))
                return false;
        }

        return true;
    }

    private static bool AllEntriesFaithful(
        Type keyType,
        Type valueType,
        object? value,
        FaithfulnessWalk walk,
        out string? why)
    {
        why = null;

        if (!CanVaryAtRuntime(keyType) && !CanVaryAtRuntime(valueType))
            return true;

        if (value is not IDictionary entries)
            return true;

        foreach (DictionaryEntry entry in entries)
        {
            if (!walk.TryInspect(out why))
                return false;

            if (CanVaryAtRuntime(keyType)
                && !IsFaithfulValue(keyType, entry.Key, "one of its keys", walk, out why))
            {
                return false;
            }

            if (CanVaryAtRuntime(valueType)
                && !IsFaithfulValue(valueType, entry.Value, "one of its values", walk, out why))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PairFaithful(Type pairType, object? value, FaithfulnessWalk walk, out string? why)
    {
        why = null;

        if (value is null)
            return true;

        foreach (var (name, position) in new[] { ("Key", "its key"), ("Value", "its value") })
        {
            var property = pairType.GetProperty(name)!;

            if (!CanVaryAtRuntime(property.PropertyType))
                continue;

            if (!IsFaithfulValue(property.PropertyType, property.GetValue(value), position, walk, out why))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     The array behind a <c>Memory&lt;T&gt;</c>, and only when its elements need inspecting -
    ///     <c>ToArray</c> copies, which is not worth paying for a <c>Memory&lt;byte&gt;</c> that cannot
    ///     vary.
    /// </summary>
    private static IEnumerable? BufferOf(Type memoryType, Type element, object? value)
        => value is null || !CanVaryAtRuntime(element)
            ? null
            : memoryType.GetMethod("ToArray", Type.EmptyTypes)?.Invoke(value, null) as IEnumerable;

    /// <summary>
    ///     Whether a subclass of <paramref name="type" /> could be sitting in a position declared as
    ///     it. Arrays are included because array assignment is covariant, so a <c>Shape[]</c> position
    ///     genuinely can hold a <c>Square[]</c> even though an array type reports itself sealed.
    /// </summary>
    private static bool CanVaryAtRuntime(Type type)
        => !type.IsValueType && (type.IsArray || !type.IsSealed);

    private static bool AllFaithful(Type[] types, FaithfulnessWalk walk, out string? why)
    {
        foreach (var type in types)
        {
            if (!IsFaithfulType(type, value: null, walk, out why))
                return false;
        }

        why = null;

        return true;
    }

    /// <summary>
    ///     Names the member System.Text.Json will drop on arrival, or <c>null</c> when it will carry
    ///     it. Verified against the serializer rather than reasoned about: a public readonly field and
    ///     a get-only property are both <i>written</i> and both discarded on read.
    /// </summary>
    private static string? SerializerCannotRestore(FieldInfo field, Type owner)
    {
        if (field.IsPublic)
            return field.IsInitOnly ? $"a public readonly field '{field.Name}'" : null;

        if (BackingPropertyOf(field, owner) is { } property)
            return property.SetMethod is { IsPublic: true } ? null : $"a get-only property '{property.Name}'";

        return $"a private field '{field.Name}'";
    }

    /// <summary>
    ///     The auto-property a compiler-generated backing field belongs to, or <c>null</c> when the
    ///     field is one the author declared themselves.
    /// </summary>
    private static PropertyInfo? BackingPropertyOf(FieldInfo field, Type owner)
    {
        if (!field.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            return null;

        var name = field.Name;

        if (!name.StartsWith('<'))
            return null;

        var close = name.IndexOf('>', StringComparison.Ordinal);

        if (close < 2)
            return null;

        return owner.GetProperty(
            name[1..close],
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    /// <summary>The member as its author wrote it - the property name, for a backing field.</summary>
    private static string MemberNameOf(FieldInfo field, Type owner)
        => BackingPropertyOf(field, owner)?.Name ?? field.Name;

    /// <summary>
    ///     Bounds the value walk, so a deep or enormous opted-in graph refuses rather than running out
    ///     of stack or spending measurable time in the coordinator.
    /// </summary>
    private sealed class FaithfulnessWalk
    {
        /// <summary>Nesting of opted-in types, which - unlike a display class chain - is unbounded.</summary>
        private const int MaxStateDepth = 16;

        /// <summary>
        ///     How many individual elements are type-checked before the walk gives up. Only positions
        ///     that <see cref="CanVaryAtRuntime" /> counts against it, so the collections that reach it
        ///     are large collections of polymorphism-capable objects - exactly the shape a prepare
        ///     delegate is better at anyway.
        /// </summary>
        private const int MaxInspectedItems = 100_000;

        private int _depth;

        private int _inspected;

        public HashSet<object> Visiting { get; } = new(ReferenceEqualityComparer.Instance);

        public bool TryDescend(Type type, out string? why)
        {
            why = null;

            if (++_depth <= MaxStateDepth)
                return true;

            why = $"'{FriendlyTypeName(type)}' nests more than {MaxStateDepth} levels of transferable "
                  + "state, which is not walked to the end. Build the value in the measuring process "
                  + "with a prepare delegate instead.";

            return false;
        }

        public void Ascend() => _depth--;

        public bool TryInspect(out string? why)
        {
            why = null;

            if (++_inspected <= MaxInspectedItems)
                return true;

            why = $"it contains more than {MaxInspectedItems:N0} elements whose type has to be checked "
                  + "one by one, because a subclass could be substituted for any of them. Build the "
                  + "value in the measuring process with a prepare delegate instead.";

            return false;
        }
    }

    /// <summary>
    ///     Whether a keyed collection uses the default comparer for its key type.
    /// </summary>
    /// <remarks>
    ///     The crux of the whole rule. A dictionary built with <c>StringComparer.OrdinalIgnoreCase</c>
    ///     serializes to exactly the same entries as one built without it, so no amount of comparing
    ///     the data afterwards could distinguish them - and the two have materially different lookup
    ///     cost, which is very often the thing being measured. The comparer is observable at runtime,
    ///     so this is a check rather than a gamble.
    /// </remarks>
    private static bool HasDefaultComparer(
        Type collectionType,
        object? value,
        Type defaultComparerDefinition,
        Type keyType,
        out string? why)
    {
        why = null;

        if (value is null)
            return true;

        var comparer = collectionType.GetProperty("Comparer")?.GetValue(value);

        if (comparer is null)
            return true;

        var expected = defaultComparerDefinition
            .MakeGenericType(keyType)
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)
            ?.GetValue(null);

        if (ReferenceEquals(comparer, expected) || Equals(comparer, expected))
            return true;

        why = $"it was built with a custom comparer ('{FriendlyTypeName(comparer.GetType())}'), which "
              + "changes its lookup cost but not its contents - so rebuilding it from those contents "
              + "would measure a differently-behaved collection under the same name.";

        return false;
    }

    /// <summary>
    ///     Every instance field a value of this type carries, including private fields declared on
    ///     base types.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Walked level by level with <see cref="BindingFlags.DeclaredOnly" /> because the plain
    ///         <c>GetFields</c> does <b>not</b> return a base type's private fields. For a Roslyn
    ///         display class - sealed, deriving from <c>object</c> - the two are identical, which is
    ///         exactly why the difference would have gone unnoticed until a user object with an
    ///         inherited private field arrived and was rebuilt with that field at its default.
    ///     </para>
    ///     <para>
    ///         Ordered by token so both sides agree on a sequence without relying on reflection's
    ///         ordering, which is documented as none.
    ///     </para>
    /// </remarks>
    public static FieldInfo[] InstanceFieldsOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var fields = new List<FieldInfo>();

        for (var level = type; level is not null && level != typeof(object); level = level.BaseType)
        {
            fields.AddRange(level.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }

        return [.. fields.OrderBy(f => f.MetadataToken)];
    }

    /// <summary>
    ///     Whether this field is a display class's reference to the instance whose method declared the
    ///     lambda - Roslyn's <c>&lt;&gt;4__this</c>.
    /// </summary>
    /// <remarks>
    ///     Identified by shape rather than by that name: a display class is emitted as a nested type of
    ///     the type that declared the lambda, so a field of exactly that type is the captured <c>this</c>.
    ///     Checking the name alone would break silently if the compiler's convention changed; checking
    ///     the shape alone cannot, because no other field can hold the declaring type by construction.
    /// </remarks>
    private static bool IsCapturedEnclosingInstance(FieldInfo field)
        => field.DeclaringType is { } owner
           && IsCompilerGeneratedScope(owner)
           && owner.DeclaringType is { } enclosing
           && field.FieldType == enclosing;

    /// <summary>Whether a type is a compiler-generated closure scope rather than user data.</summary>
    public static bool IsCompilerGeneratedScope(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) && type.IsClass;
    }

    private static bool IsBlittablePrimitiveArray(Type type)
        => type.IsArray
           && type.GetArrayRank() == 1
           && type.GetElementType() is { IsPrimitive: true } element
           && element != typeof(bool)
           && element != typeof(char);

    private static byte[] ToBytes(Array array)
    {
        var bytes = new byte[Buffer.ByteLength(array)];

        Buffer.BlockCopy(array, 0, bytes, 0, bytes.Length);

        return bytes;
    }

    /// <summary>Rebuilds a blittable primitive array from its raw bytes.</summary>
    public static Array FromBytes(Type arrayType, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(arrayType);
        ArgumentNullException.ThrowIfNull(bytes);

        var element = arrayType.GetElementType()!;
        var size = Marshal.SizeOf(element);
        var array = Array.CreateInstance(element, bytes.Length / size);

        Buffer.BlockCopy(bytes, 0, array, 0, bytes.Length);

        return array;
    }

    internal static string NameOf(Type type)
        => type.AssemblyQualifiedName ?? type.FullName ?? type.Name;

    /// <summary>
    ///     A display-class field's name as the user wrote it. Roslyn names the field after the local
    ///     it hoisted, but decorates a few - <c>&lt;&gt;4__this</c> for a captured <c>this</c>, and
    ///     <c>CS$&lt;&gt;8__locals</c> for a linked scope - and printing those verbatim asks the reader
    ///     to decode compiler output.
    /// </summary>
    private static string FriendlyFieldName(FieldInfo field)
        => field.Name switch
        {
            "<>4__this" => "this",
            var n when n.StartsWith("CS$<>", StringComparison.Ordinal) => "an enclosing scope",
            var n => n,
        };

    private static string FriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;

        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);

        if (tick > 0)
            name = name[..tick];

        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName))}>";
    }
}
