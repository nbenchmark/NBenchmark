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

    public string? Json { get; init; }

    public byte[]? Binary { get; init; }

    public IReadOnlyList<CapturedField>? Nested { get; init; }
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
    public static bool TryCapture(
        object receiver,
        string subject,
        int budgetBytes,
        out IReadOnlyList<CapturedField> captured,
        out Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(receiver);

        captured = [];
        refusal = Refusal.None;

        // Identity is tracked across the whole set, not per field. Two fields pointing at one array
        // are observably shared - a body that mutates through one sees it through the other - and
        // rebuilding them as two arrays would measure a program the user did not write.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);

        var total = 0;

        if (!TryCaptureInto(receiver, subject, seen, budgetBytes, depth: 0, ref total, out captured, out refusal))
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
                $"{subject} '{FriendlyFieldName(field)}' as well as another field referring to the "
                + "same object. Rebuilding them would produce two objects where the benchmark sees "
                + "one, so the sharing has to be reproduced by a prepare delegate rather than sent.");

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
    ///     Note the runtime-type check. A field declared <c>IList&lt;int&gt;</c> holding a
    ///     <c>MyPagedList</c> would round-trip into a <c>List&lt;int&gt;</c> with the same entries and
    ///     entirely different performance, so a value whose runtime type differs from its declared
    ///     type is refused rather than flattened.
    /// </remarks>
    public static bool IsFaithful(Type declared, object? value, out string? why)
    {
        ArgumentNullException.ThrowIfNull(declared);

        why = null;

        if (value is null)
        {
            if (declared.IsValueType && Nullable.GetUnderlyingType(declared) is null)
            {
                why = "a null was found in a non-nullable field.";

                return false;
            }

            return true;
        }

        var runtime = value.GetType();

        if (runtime != declared && !declared.IsValueType)
        {
            why = $"the value is a '{FriendlyTypeName(runtime)}' held in a '{FriendlyTypeName(declared)}' "
                  + "field, and rebuilding it would substitute the declared type for the real one.";

            return false;
        }

        return IsFaithfulType(declared, value, out why);
    }

    private static bool IsFaithfulType(Type type, object? value, out string? why)
    {
        why = null;

        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (TestArgumentCodec.IsSupported(underlying))
            return true;

        if (underlying.IsDefined(typeof(BenchmarkStateAttribute), inherit: false))
            return true;

        if (underlying.IsArray)
        {
            if (underlying.GetArrayRank() != 1)
            {
                why = "multi-dimensional arrays are not transferred.";

                return false;
            }

            return IsFaithfulType(underlying.GetElementType()!, value: null, out why);
        }

        if (underlying.IsGenericType)
        {
            var definition = underlying.GetGenericTypeDefinition();
            var arguments = underlying.GetGenericArguments();

            if (definition == typeof(List<>)
                || definition == typeof(IReadOnlyList<>)
                || definition == typeof(KeyValuePair<,>)
                || definition == typeof(Memory<>)
                || definition == typeof(ReadOnlyMemory<>))
            {
                return AllFaithful(arguments, out why);
            }

            if (definition == typeof(Dictionary<,>) || definition == typeof(HashSet<>))
            {
                return AllFaithful(arguments, out why)
                       && HasDefaultComparer(underlying, value, typeof(EqualityComparer<>), arguments[0], out why);
            }

            if (definition == typeof(SortedDictionary<,>) || definition == typeof(SortedSet<>))
            {
                return AllFaithful(arguments, out why)
                       && HasDefaultComparer(underlying, value, typeof(Comparer<>), arguments[0], out why);
            }
        }

        why = $"'{FriendlyTypeName(type)}' is not one of the types whose measured behaviour is fully "
              + "determined by its contents. Mark it [BenchmarkState] if it is, or build it in the "
              + "measuring process with a prepare delegate.";

        return false;
    }

    private static bool AllFaithful(Type[] types, out string? why)
    {
        foreach (var type in types)
        {
            if (!IsFaithfulType(type, value: null, out why))
                return false;
        }

        why = null;

        return true;
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

    private static string NameOf(Type type)
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
