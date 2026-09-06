using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using NBenchmark.Engine;
using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     Turns a <see cref="BodyRef" /> back into an invokable delegate inside the worker.
///     <para>
///         The single enforcement point for cross-process body addressing. Nothing is lifted,
///         regenerated or deserialized - the already-compiled method the user's compiler emitted is
///         located and bound, so the code that gets measured cannot differ from the code that was
///         written.
///     </para>
/// </summary>
internal static class BodyResolver
{
    /// <summary>
    ///     Resolves a body, or returns <c>false</c> with a message suitable for reporting as the
    ///     benchmark's error.
    /// </summary>
    public static bool TryResolve(
        BenchmarkLoadContext context,
        BodyRef body,
        ResolvedReceivers receivers,
        out Delegate resolved,
        out string? error)
        => TryResolve(context, body, receivers, out resolved, out _, out error);

    /// <inheritdoc cref="TryResolve(BenchmarkLoadContext, BodyRef, ResolvedReceivers, out Delegate, out string?)" />
    /// <param name="boundArguments">
    ///     The values this body's parameters were filled with, in declaration order. Empty for a
    ///     parameterless body.
    ///     <para>
    ///         Handed back so a lifecycle hook can be bound to the <b>same</b> values rather than to
    ///         fresh ones. A <c>setup</c> that resets prepared state has to act on the array the body
    ///         reads; re-running the recipe for the hook would build a second array and reset that one,
    ///         which is the private-copy failure shared receivers exist to prevent.
    ///     </para>
    /// </param>
    public static bool TryResolve(
        BenchmarkLoadContext context,
        BodyRef body,
        ResolvedReceivers receivers,
        out Delegate resolved,
        out IReadOnlyList<object?> boundArguments,
        out string? error)
    {
        resolved = null!;
        boundArguments = [];

        if (!TryBindMethod(context, body, receivers, out var method, out var receiver, out error))
            return false;

        if (!TryDelegateType(method, body, out var delegateType, out error))
            return false;

        Delegate created;

        try
        {
            created = method.CreateDelegate(delegateType, receiver);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or MissingMethodException
                                       or InvalidOperationException)
        {
            // InvalidOperationException is what an incompletely-closed generic produces. Binding a
            // body is allowed to fail - the row says why and the group carries on - so every way it
            // can fail belongs in this filter rather than escaping to the group handler.
            error = $"the resolved method '{method.Name}' could not be bound as {delegateType.Name}: {ex.Message}";
            return false;
        }

        if (body.Arguments.Count == 0)
        {
            resolved = created;
            return true;
        }

        return TryBindArguments(context, created, body, receivers, out resolved, out boundArguments, out error);
    }

    /// <summary>
    ///     Resolves a per-iteration hook, binding it to the body's own prepared values when it asks for
    ///     them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A hook that takes no parameters is bound as-is. One whose arity matches the body's is
    ///         bound to the <i>already-resolved</i> values, which is what makes
    ///         <c>setup: (int[] d) =&gt; Shuffle(d)</c> shuffle the array the body then sorts. Without
    ///         it the canonical sort benchmark cannot be written correctly at all: the recipe runs once,
    ///         so from the second sample onward the body sorts an already-sorted array and reports the
    ///         cost of doing nothing.
    ///     </para>
    ///     <para>
    ///         An arity that is neither is refused rather than partially bound. The hook is not the
    ///         benchmark, but a benchmark measured with its setup silently dropped produces a plausible
    ///         number for work that never happened.
    ///     </para>
    /// </remarks>
    public static bool TryResolveHook(
        BenchmarkLoadContext context,
        BodyRef hook,
        ResolvedReceivers receivers,
        IReadOnlyList<object?> boundArguments,
        out Action resolved,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(hook);
        ArgumentNullException.ThrowIfNull(boundArguments);

        resolved = null!;

        if (!TryBindMethod(context, hook, receivers, out var method, out var receiver, out error))
            return false;

        if (method.ReturnType != typeof(void))
        {
            error = $"the resolved hook '{method.Name}' returns {method.ReturnType.Name}; a per-iteration "
                    + "hook must return void.";

            return false;
        }

        var parameters = method.GetParameters();

        if (parameters.Length != 0 && parameters.Length != boundArguments.Count)
        {
            error = $"the resolved hook '{method.Name}' takes {parameters.Length} parameter(s), which is "
                    + $"neither none nor the {boundArguments.Count} the body takes, so there is nothing to "
                    + "call it with.";

            return false;
        }

        Delegate created;

        try
        {
            var delegateType = typeof(Action);

            if (parameters.Length != 0 && !ArgumentBinder.TryDelegateTypeFor(method, out delegateType, out error))
                return false;

            created = method.CreateDelegate(delegateType, receiver);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or MissingMethodException
                                       or InvalidOperationException)
        {
            error = $"the resolved hook '{method.Name}' could not be bound: {ex.Message}";

            return false;
        }

        if (parameters.Length != 0)
        {
            if (!ArgumentBinder.TryBind(created, boundArguments, out created, out var bindError))
            {
                error = bindError;

                return false;
            }
        }

        if (created is not Action action)
        {
            error = $"the resolved hook '{method.Name}' bound to {created.GetType().Name} rather than an Action.";

            return false;
        }

        resolved = action;

        return true;
    }

    /// <summary>
    ///     Locates the method an address names, and recovers the receiver it must be bound to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The half of resolution that is about <i>finding</i> code rather than about shaping it
    ///         into a benchmark: load the defining assembly, check the module version id, resolve the
    ///         token, recover the closure receiver. Every enforcement point that makes cross-process
    ///         addressing safe lives here.
    ///     </para>
    ///     <para>
    ///         Separate from <see cref="TryResolve(BenchmarkLoadContext, BodyRef, ResolvedReceivers, out Delegate, out string?)" /> because a factory and a benchmark body need the
    ///         same locating and different shaping. A body with parameters and no argument values is
    ///         unmeasurable and is refused; a <c>Func&lt;Type, object&gt;</c> instance factory has
    ///         parameters by definition and is supplied its argument at invocation. Routing the second
    ///         through the first rejected it for a rule that was written about the first.
    ///     </para>
    /// </remarks>
    public static bool TryBindMethod(
        BenchmarkLoadContext context,
        BodyRef body,
        ResolvedReceivers receivers,
        out MethodInfo method,
        out object? receiver,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        method = null!;
        receiver = null;
        error = null;

        Assembly defining;

        try
        {
            defining = context.LoadFromAssemblyName(new AssemblyName(body.AssemblySimpleName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            error = $"the assembly '{body.AssemblySimpleName}' that defines it could not be loaded: {ex.Message}";
            return false;
        }

        var module = defining.ManifestModule;

        if (module.ModuleVersionId != body.ModuleVersionId)
        {
            // Refusing here is what stops a stale address from measuring the wrong method under
            // the right name. Inserting a lambda changes the MVID but leaves the old token valid,
            // pointing at a different body - so an unchecked token fails silently rather than
            // loudly, which is the worst possible failure for a measurement tool.
            error = $"'{body.AssemblySimpleName}' on disk (module {module.ModuleVersionId}) is not the "
                    + $"build the benchmark was addressed against (module {body.ModuleVersionId}). "
                    + "Rebuild and re-run.";

            return false;
        }

        try
        {
            if (module.ResolveMethod(body.MethodToken) is not MethodInfo resolvedMethod)
            {
                error = $"metadata token 0x{body.MethodToken:X8} in '{body.AssemblySimpleName}' is not a method.";
                return false;
            }

            method = resolvedMethod;
        }
        catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
        {
            error = $"metadata token 0x{body.MethodToken:X8} could not be resolved in "
                    + $"'{body.AssemblySimpleName}': {ex.Message}";

            return false;
        }

        var bound = body.Shape switch
        {
            BodyShape.CachedSingleton => TryResolveReceiver(context, ref method, body, out receiver, out error),
            BodyShape.TransferredReceiver =>
                TryTransferReceiver(context, ref method, body, receivers, out receiver, out error),

            // A static method needs no receiver - but it can still be declared on a *closed generic
            // type*, and the token resolves to the open definition. Nothing closed it: only the two
            // receiver shapes reached TryCloseDeclaringType, and TryCloseMethod below closes method
            // generics only. So `Benchmark.Run(Box<int>.Count)` bound `Box<T>.Count` and CreateDelegate
            // threw "the containing type is not fully instantiated" - an InvalidOperationException,
            // which was not in the filter that catches bind failures, so it escaped and faulted the
            // whole group over one body.
            BodyShape.StaticMethod => TryCloseDeclaringType(context, ref method, body, out _, out error),

            // Switched rather than tested for one shape, so a new one cannot silently take the
            // null-receiver path - which is how a transferred receiver first arrived bound to nothing
            // and failed inside the measurement rather than at bind time.
            _ => true,
        };

        // After the declaring type, never before: closing the type re-resolves the method from its
        // handle against the closed type, which would discard a closure applied here. Applied to every
        // shape, including a plain static generic method, which has no receiver and so never reached
        // the type-closing step at all.
        return bound && TryCloseMethod(context, ref method, body, out error);
    }

    /// <summary>
    ///     Closes a generic method over the type arguments the address carried.
    /// </summary>
    /// <remarks>
    ///     A metadata token names the open definition, so <c>Sort&lt;int&gt;</c> resolves to
    ///     <c>Sort&lt;T&gt;</c> - which cannot be invoked. Carrying the arguments is what makes a
    ///     generic body measurable instead of refused.
    /// </remarks>
    private static bool TryCloseMethod(
        BenchmarkLoadContext context,
        ref MethodInfo method,
        BodyRef body,
        out string? error)
    {
        error = null;

        if (!method.IsGenericMethodDefinition)
            return true;

        if (body.MethodGenericArguments is not { Count: > 0 } names)
        {
            error = $"'{method.Name}' is a generic method but the address carries no type arguments for it.";

            return false;
        }

        if (!GenericArguments.TryResolve(names, name => TypeNames.Resolve(name, context), out var arguments,
                out var unresolved))
        {
            error = $"type argument '{unresolved}' could not be resolved in the worker.";

            return false;
        }

        try
        {
            method = method.MakeGenericMethod(arguments);
        }
        catch (Exception ex) when (ex is ArgumentException or TypeLoadException)
        {
            error = $"'{method.Name}' could not be closed over the carried type arguments: {ex.Message}";

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Supplies a body's argument values, leaving the parameterless delegate the engine measures.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         One walk over the address's argument slots, each either an encoded constant to decode or
    ///         a recipe to run <b>here</b>, once, before the body is ever measured - which is the entire
    ///         point of a recipe. The value never crosses the boundary; only the instructions for it do.
    ///         That is what lets a benchmark over a prepared array, an open connection or a warmed cache
    ///         be isolated at all, where serializing the prepared value would either fail or, worse,
    ///         succeed at producing something subtly different.
    ///     </para>
    ///     <para>
    ///         The declared parameter types are read from the <b>resolved method</b> rather than trusted
    ///         from the payload, which is the same rule the test-method path follows. A payload's type
    ///         name is a claim about the far side; the method's own signature is the fact. Decoding
    ///         against the claim would let a stale or mismatched request bind a plausible value of the
    ///         wrong type.
    ///     </para>
    ///     <para>
    ///         A recipe's own exceptions are reported as this benchmark's failure. It is user code
    ///         running before measurement, so a throw there means the benchmark never had valid input -
    ///         which is worth saying plainly rather than surfacing as a dead worker.
    ///     </para>
    /// </remarks>
    private static bool TryBindArguments(
        BenchmarkLoadContext context,
        Delegate created,
        BodyRef body,
        ResolvedReceivers receivers,
        out Delegate resolved,
        out IReadOnlyList<object?> boundArguments,
        out string? error)
    {
        resolved = created;
        boundArguments = [];
        error = null;

        var parameters = created.Method.GetParameters();

        if (parameters.Length != body.Arguments.Count)
        {
            error = $"'{created.Method.Name}' takes {parameters.Length} parameter(s) but the address "
                    + $"carries {body.Arguments.Count} argument value(s).";

            return false;
        }

        var bound = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var source = body.Arguments[i];

            if (!source.IsWellFormed(out var problem))
            {
                error = $"the address for parameter '{parameters[i].Name}' {problem}";

                return false;
            }

            if (source.Recipe is { } recipe)
            {
                // The expected type is the body's own parameter type, read from the method resolved
                // here rather than trusted from the plan: both delegates came from metadata tokens, and
                // a disagreement means the address no longer describes the code on disk. FactoryResolver
                // checks it against the factory's declared return type before running any user code.
                if (!FactoryResolver.TryInvoke(
                        context,
                        body.AssemblyPath,
                        recipe,
                        receivers,
                        parameters[i].ParameterType,
                        out bound[i],
                        out error,
                        out _))
                {
                    return false;
                }

                continue;
            }

            try
            {
                bound[i] = TestArgumentCodec.Decode(source.Value!, parameters[i].ParameterType);
            }
            catch (Exception ex) when (ex is FormatException
                                          or OverflowException
                                          or ArgumentException
                                          or InvalidOperationException)
            {
                error = $"argument '{parameters[i].Name}' could not be decoded as "
                        + $"{parameters[i].ParameterType.Name}: {ex.Message}";

                return false;
            }
        }

        if (!ArgumentBinder.TryBind(created, bound, out resolved, out var bindError))
        {
            error = bindError;

            return false;
        }

        boundArguments = bound;

        return true;
    }

    /// <summary>
    ///     Recovers the receiver for a non-capturing lambda: Roslyn's cached closure singleton.
    ///     <para>
    ///         When the body was declared inside a generic method, Roslyn puts its closure class on
    ///         a generic type, and a metadata token resolves to the method on the <i>open</i> type.
    ///         The singleton field only exists on a closed type, so the type is closed over the
    ///         carried arguments and <paramref name="method" /> is re-resolved against it - which is
    ///         why it is taken by reference.
    ///     </para>
    /// </summary>
    private static bool TryResolveReceiver(
        BenchmarkLoadContext context,
        ref MethodInfo method,
        BodyRef body,
        out object? receiver,
        out string? error)
    {
        receiver = null;
        error = null;

        if (!TryCloseDeclaringType(context, ref method, body, out var declaringType, out error))
            return false;

        var singleton = BodyRef.FindSingletonField(declaringType!);

        if (singleton is null)
        {
            error = $"no cached closure instance was found on '{declaringType!.Name}'.";
            return false;
        }

        receiver = singleton.GetValue(null);

        if (receiver is null)
        {
            // The field is initialized lazily on first use of the lambda in the defining process.
            // In a fresh worker nothing has touched it, so construct the closure - which is safe
            // precisely because addressing already proved it holds no state.
            try
            {
                receiver = Activator.CreateInstance(declaringType!, nonPublic: true);
            }
            catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
            {
                error = $"the stateless closure '{declaringType!.Name}' could not be constructed: {ex.Message}";
                return false;
            }
        }

        return receiver is not null;
    }

    /// <summary>
    ///     Rebuilds a receiver that held values, and restores those values onto it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The object is allocated <b>uninitialized</b> rather than constructed. What is being
    ///         restored is observed state, not a construction: running a constructor would overwrite
    ///         the transferred fields with whatever it computes, and a display class has no meaningful
    ///         constructor to run anyway. Every field is then set from the wire, so nothing is left at
    ///         a default - which is precisely the failure of the fabricated-closure probe this
    ///         mechanism replaces.
    ///     </para>
    ///     <para>
    ///         Fields resolve by metadata token, which the module version id gate has already proved
    ///         exact. The declared type name is re-checked against what the token resolved to: the
    ///         gate makes that redundant in every case anyone has constructed, and it costs a string
    ///         comparison to be sure a mismatched build cannot write a value of one type into a field
    ///         of another.
    ///     </para>
    /// </remarks>
    private static bool TryTransferReceiver(
        BenchmarkLoadContext context,
        ref MethodInfo method,
        BodyRef body,
        ResolvedReceivers receivers,
        out object? receiver,
        out string? error)
    {
        receiver = null;

        if (!TryCloseDeclaringType(context, ref method, body, out var declaringType, out error))
            return false;

        if (body.ReceiverIndex is not { } index)
        {
            error = "its receiver holds state but the address names no entry in the group's receiver table.";

            return false;
        }

        return receivers.TryGet(index, declaringType!, out receiver, out error);
    }

    internal static bool TryBuild(
        Type type,
        IReadOnlyList<CapturedField> captures,
        BenchmarkLoadContext context,
        out object? built,
        out string? error)
    {
        built = null;
        error = null;

        object instance;

        try
        {
            instance = RuntimeHelpers.GetUninitializedObject(type);
        }
        catch (Exception ex) when (ex is MemberAccessException or ArgumentException or TypeLoadException)
        {
            error = $"the receiver '{type.Name}' could not be allocated in the worker: {ex.Message}";
            return false;
        }

        // Walked once for the whole receiver, and by the same function the coordinator used - so the
        // two sides are looking at one list rather than at two derivations of it.
        var fields = StateTransfer.InstanceFieldsOf(type);

        // Which fields the payload actually accounted for. The doc above claims every field is set
        // from the wire, and until this array existed the claim was unenforced: a payload naming
        // fewer fields than the type has left the rest at whatever GetUninitializedObject produced,
        // and one naming the same field twice silently took the last value. Both are the
        // fabricated-closure failure this mechanism replaced - a body over a prepared million-element
        // array sorting an empty one and reporting a tight interval for no work.
        var assigned = new bool[fields.Length];

        foreach (var capture in captures)
        {
            if (!TryResolveField(type, fields, capture, out var field, out var slot, out error))
                return false;

            if (assigned[slot])
            {
                error = $"the address names '{field!.Name}' more than once, so which value the field "
                        + "should end up holding is not decidable.";

                return false;
            }

            assigned[slot] = true;

            object? value;

            if (capture.Kind == CapturedValueKind.Nested)
            {
                // The runtime type the coordinator read the fields off, not the field's declared type.
                // A lambda declared in a base class and registered from a derived instance holds its
                // `this` in a base-typed field, and rebuilding that would restore the derived object's
                // fields onto the wrong class.
                if (!TryResolveNestedType(capture, field!, context, out var nestedType, out error))
                    return false;

                if (!TryBuild(nestedType!, capture.Nested ?? [], context, out value, out error))
                    return false;
            }
            else
            {
                // Decoded as the type the value *was*, which is not always the type the field is
                // declared as: an `IReadOnlyList<int>` field can hold a `List<int>`, and rebuilding it
                // as the interface is not a thing that can be done. The coordinator vetted the runtime
                // type against the same allow-list, and only names it when it differs.
                if (!TryResolveDecodeType(capture, field!, context, out var decodeAs, out error))
                    return false;

                if (!TryDecode(capture, decodeAs!, context, out value, out error))
                    return false;
            }

            try
            {
                field!.SetValue(instance, value);
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or FieldAccessException
                                           or InvalidOperationException)
            {
                error = $"the captured value for '{capture.FieldName}' could not be assigned: {ex.Message}";
                return false;
            }
        }

        for (var i = 0; i < fields.Length; i++)
        {
            if (assigned[i])
                continue;

            error = $"the address carries no value for '{type.Name}.{fields[i].Name}', so it would be "
                    + "measured against a default rather than against what the benchmark closed over.";

            return false;
        }

        built = instance;

        return true;
    }

    /// <summary>
    ///     The type to decode a captured value as: the runtime type the coordinator encoded it against
    ///     when it named one, and otherwise the field's own.
    /// </summary>
    private static bool TryResolveDecodeType(
        CapturedField capture,
        FieldInfo field,
        BenchmarkLoadContext context,
        out Type? decodeAs,
        out string? error)
    {
        decodeAs = field.FieldType;
        error = null;

        if (capture.RuntimeTypeName is not { } name)
            return true;

        if (TypeNames.Resolve(name, context) is not { } resolved)
        {
            error = $"the captured value for '{capture.FieldName}' names a runtime type '{name}' that "
                    + "could not be resolved in this worker.";

            return false;
        }

        // The field has to accept what comes back. Checked here rather than left to SetValue, which
        // reports the same disagreement as an argument exception naming neither side usefully.
        if (!field.FieldType.IsAssignableFrom(resolved))
        {
            error = $"the captured value for '{capture.FieldName}' names a runtime type "
                    + $"'{resolved.Name}' that cannot be stored in a '{field.FieldType.Name}' field.";

            return false;
        }

        decodeAs = resolved;

        return true;
    }

    /// <summary>
    ///     The type to rebuild a nested scope as: the one the coordinator actually walked, checked
    ///     against what the field will accept.
    /// </summary>
    private static bool TryResolveNestedType(
        CapturedField capture,
        FieldInfo field,
        BenchmarkLoadContext context,
        out Type? nested,
        out string? error)
    {
        nested = null;
        error = null;

        if (capture.RuntimeTypeName is not { } name)
        {
            error = $"the captured scope '{capture.FieldName}' carries no runtime type, so there is "
                    + "nothing to rebuild it as. Rebuild the benchmark project and re-run.";

            return false;
        }

        if (TypeNames.Resolve(name, context) is not { } resolved)
        {
            error = $"the captured scope '{capture.FieldName}' names a runtime type '{name}' that could "
                    + "not be resolved in this worker.";

            return false;
        }

        if (!field.FieldType.IsAssignableFrom(resolved))
        {
            error = $"the captured scope '{capture.FieldName}' names a runtime type "
                    + $"'{resolved.Name}' that cannot be stored in a '{field.FieldType.Name}' field.";

            return false;
        }

        nested = resolved;

        return true;
    }

    /// <summary>
    ///     Finds the field a capture names among the receiver's own instance fields.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Matched against <see cref="StateTransfer.InstanceFieldsOf" /> - the same walk, over the
    ///         same type, that produced the captures on the other side - rather than resolved off a
    ///         module. Resolving off <c>type.Module</c> answered three questions wrongly at once.
    ///     </para>
    ///     <para>
    ///         A <b>generic</b> display class resolved to the open definition's field, whose type is
    ///         still <c>T</c> and which cannot be set on an instance of the closed type. The repair for
    ///         that - <c>GetFieldFromHandle(openHandle, closedTypeHandle)</c> - is rejected by the
    ///         runtime outright ("field handle with declaring type ... are incompatible"), so the branch
    ///         written to support a capturing lambda in a generic context could only ever throw, and
    ///         reported it as a bad token. A closed type's own <c>GetFields</c> carries the same
    ///         metadata token with the type argument substituted, which is what was wanted throughout.
    ///     </para>
    ///     <para>
    ///         A <b>base type in another assembly</b> contributes tokens belonging to <i>its</i> module,
    ///         and a module-scoped lookup either failed or - worse - resolved an unrelated field that
    ///         happened to share the number and the declared type, writing the value somewhere else
    ///         and leaving the real field at its default. Walking the hierarchy finds each level's
    ///         fields in the scope that defines them.
    ///     </para>
    ///     <para>
    ///         And a token names anything in a module, including a <b>static</b> field, on which
    ///         <c>SetValue</c> quietly ignores its instance argument and mutates process-wide state.
    ///         Nothing here validated that; the walk excludes statics by construction, and cannot
    ///         return a field belonging to some other type.
    ///     </para>
    /// </remarks>
    private static bool TryResolveField(
        Type type,
        FieldInfo[] fields,
        CapturedField capture,
        out FieldInfo? field,
        out int slot,
        out string? error)
    {
        error = null;

        // Token and name together, because a token is unique only within its own module and a base
        // type can be declared in another assembly. The name settles which level was meant; it is a
        // tiebreak rather than the address, so a rename still cannot bind a different field.
        slot = FindField(fields, capture.FieldToken, capture.FieldName, out var ambiguous);

        if (slot < 0 && !ambiguous)
            slot = FindField(fields, capture.FieldToken, name: null, out ambiguous);

        field = slot < 0 ? null : fields[slot];

        if (ambiguous)
        {
            error = $"'{type.Name}' has more than one instance field matching '{capture.FieldName}' "
                    + $"(token 0x{capture.FieldToken:X8}), so which one the address meant is not decidable.";

            return false;
        }

        if (field is null)
        {
            error = $"no instance field matching '{capture.FieldName}' (token 0x{capture.FieldToken:X8}) "
                    + $"was found on '{type.Name}'.";

            return false;
        }

        var actual = field.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? field.FieldType.Name;

        if (!string.Equals(actual, capture.DeclaredTypeName, StringComparison.Ordinal))
        {
            error = $"the field '{field.Name}' is declared '{actual}' here but the "
                    + $"address carries a value for '{capture.DeclaredTypeName}'.";

            return false;
        }

        return true;
    }

    /// <summary>
    ///     The index of the single field matching a token - and, when one is given, a name - or
    ///     <c>-1</c>, with <paramref name="ambiguous" /> set when more than one matched.
    /// </summary>
    private static int FindField(FieldInfo[] fields, int token, string? name, out bool ambiguous)
    {
        var found = -1;

        ambiguous = false;

        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].MetadataToken != token)
                continue;

            if (name is not null && !string.Equals(fields[i].Name, name, StringComparison.Ordinal))
                continue;

            if (found >= 0)
            {
                ambiguous = true;

                return -1;
            }

            found = i;
        }

        return found;
    }

    private static bool TryDecode(
        CapturedField capture,
        Type declared,
        BenchmarkLoadContext context,
        out object? value,
        out string? error)
    {
        value = null;
        error = null;

        try
        {
            // Each arm requires its own payload rather than defaulting one in. `Binary ?? []` made an
            // absent array an *empty* array, `Json ?? "null"` made an absent value null: both turn a
            // malformed frame into a plausible wrong number instead of a refusal, which is the failure
            // this whole mechanism exists to prevent. The default arm is a refusal too - the kind
            // crosses as a JSON integer, so an out-of-range one used to be read as JSON and produce a
            // null.
            switch (capture.Kind)
            {
                case CapturedValueKind.Binary when capture.Binary is { } bytes && capture.ArrayDimensions is { } dimensions:
                    value = StateTransfer.FromBytes(declared, bytes, dimensions);

                    return true;

                case CapturedValueKind.Json when capture.Json is { } json:
                    value = JsonSerializer.Deserialize(json, declared, StateTransfer.SerializerOptions);

                    // The plain deserialize above builds the entries correctly either way; only a
                    // named, non-default comparer needs the collection rebuilt on top of them.
                    return capture.ComparerName is null
                           || TryApplyComparer(capture, declared, context, ref value, out error);

                case CapturedValueKind.Binary:
                    error = $"the captured value for '{capture.FieldName}' says it travels as Binary "
                            + "but carries no Binary payload, or no shape for it - so its element "
                            + "count cannot be known without guessing, and this does not guess.";

                    return false;

                case CapturedValueKind.Json:
                    error = $"the captured value for '{capture.FieldName}' says it travels as Json "
                            + "but carries no Json payload.";

                    return false;

                default:
                    error = $"the captured value for '{capture.FieldName}' names an unknown transfer "
                            + $"kind ({(int)capture.Kind}).";

                    return false;
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            error = $"the captured value for '{capture.FieldName}' could not be decoded as "
                    + $"'{declared.Name}': {ex.Message}";

            return false;
        }
    }

    /// <summary>
    ///     Rebuilds a keyed collection with the comparer it was actually built with. The entries the
    ///     plain deserialize already produced are correct as they stand; only the lookup structure
    ///     they are indexed by needs to change.
    /// </summary>
    private static bool TryApplyComparer(
        CapturedField capture,
        Type declared,
        BenchmarkLoadContext context,
        ref object? value,
        out string? error)
    {
        error = null;

        if (value is null || capture.ComparerName is not { } name)
            return true;

        if (!TryResolveComparer(name, context, out var comparer, out var reason))
        {
            error = $"the captured value for '{capture.FieldName}' {reason}";

            return false;
        }

        try
        {
            // Every one of the four collection types R4 supports has this constructor - the existing
            // entries plus the comparer to index them by - so one call rebuilds any of them.
            value = Activator.CreateInstance(declared, value, comparer);

            return true;
        }
        catch (Exception ex) when (ex is MissingMethodException or TargetInvocationException or ArgumentException)
        {
            error = $"the captured value for '{capture.FieldName}' names comparer '{name}' but "
                    + $"'{declared.Name}' could not be rebuilt with it: {ex.Message}";

            return false;
        }
    }

    /// <summary>
    ///     Resolves a comparer identity <see cref="StateTransfer" /> put on the wire: <c>"F:"</c> for a
    ///     named framework singleton, resolved by name with no type loading at all; <c>"T:"</c> for a
    ///     stateless user comparer, resolved the same way any other captured type is.
    /// </summary>
    private static bool TryResolveComparer(
        string name,
        BenchmarkLoadContext context,
        out object? comparer,
        out string? reason)
    {
        comparer = null;
        reason = null;

        if (name.StartsWith("F:", StringComparison.Ordinal))
        {
            if (StateTransfer.TryResolveKnownStringComparer(name[2..], out var known))
            {
                comparer = known;

                return true;
            }

            reason = $"names a comparer ('{name}') this worker does not recognize.";

            return false;
        }

        if (name.StartsWith("T:", StringComparison.Ordinal))
        {
            var typeName = name[2..];

            if (TypeNames.Resolve(typeName, context) is not { } type)
            {
                reason = $"names a comparer type '{typeName}' that could not be resolved in this worker.";

                return false;
            }

            try
            {
                comparer = Activator.CreateInstance(type);

                return true;
            }
            catch (Exception ex) when (ex is MissingMethodException or MemberAccessException
                                            or TargetInvocationException)
            {
                reason = $"names a comparer type '{typeName}' that could not be constructed: {ex.Message}";

                return false;
            }
        }

        reason = $"names a comparer ('{name}') in a form this worker does not recognize.";

        return false;
    }

    /// <summary>
    ///     Closes the receiver's declaring type over the carried type arguments, when the body was
    ///     declared inside a generic method and Roslyn put its closure class on a generic type. A
    ///     metadata token resolves to the method on the <i>open</i> type, so
    ///     <paramref name="method" /> is re-resolved against the closed one - which is why it is taken
    ///     by reference.
    /// </summary>
    private static bool TryCloseDeclaringType(
        BenchmarkLoadContext context,
        ref MethodInfo method,
        BodyRef body,
        out Type? declaringType,
        out string? error)
    {
        error = null;
        declaringType = method.DeclaringType;

        if (declaringType is null)
        {
            error = "the resolved method has no declaring type to bind to.";
            return false;
        }

        if (declaringType.IsGenericTypeDefinition)
        {
            if (body.TypeGenericArguments is not { Count: > 0 } names)
            {
                error = $"'{declaringType.Name}' is generic but the address carries no type arguments.";
                return false;
            }

            // Resolved through the target's own graph, not the worker's default context: a user's type
            // argument is never in the latter, and a lookup there fails for a type that is certainly
            // present.
            if (!GenericArguments.TryResolve(names, name => TypeNames.Resolve(name, context), out var arguments,
                    out var unresolved))
            {
                error = $"type argument '{unresolved}' could not be resolved in the worker.";

                return false;
            }

            try
            {
                declaringType = declaringType.MakeGenericType(arguments);

                method = (MethodInfo)MethodBase.GetMethodFromHandle(method.MethodHandle, declaringType.TypeHandle)!;
            }
            catch (Exception ex) when (ex is ArgumentException or TypeLoadException)
            {
                error = $"'{declaringType.Name}' could not be closed over the carried type arguments: {ex.Message}";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Infers the delegate type from the method's own signature, so the worker binds the exact
    ///     shape the engine measures.
    ///     <para>
    ///         Reconstructing the precise <c>Func&lt;T&gt;</c> rather than adapting through a
    ///         <c>Func&lt;object&gt;</c> matters: boxing the return value of a value-typed body adds
    ///         both time and a per-operation allocation that the report would attribute to the
    ///         user's code. Single mode measures unboxed today and must keep doing so once it runs
    ///         in a worker.
    ///     </para>
    /// </summary>
    private static bool TryDelegateType(MethodInfo method, BodyRef body, out Type delegateType, out string? error)
    {
        delegateType = typeof(Action);
        error = null;

        if (method.GetParameters().Length != 0)
        {
            // A parameterized body is bound as its own Action<…>/Func<…, T> here and has its arguments
            // supplied immediately afterwards. It reaches this point only when the address says where
            // those values come from - serialized constants for a parameter sweep, or a factory to run
            // for prepared state. A body with parameters and neither is not addressable, because there
            // is nothing to call it with.
            if (body.Arguments.Count == 0)
            {
                error = $"'{method.Name}' takes {method.GetParameters().Length} parameter(s) but the "
                        + "address carries neither argument values nor a prepare delegate to supply "
                        + "them.";

                return false;
            }

            return ArgumentBinder.TryDelegateTypeFor(method, out delegateType, out error);
        }

        var returnType = method.ReturnType;

        if (returnType == typeof(void))
            return true;

        if (returnType == typeof(Task))
        {
            delegateType = typeof(Func<Task>);
            return true;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            delegateType = typeof(Func<>).MakeGenericType(returnType);
            return true;
        }

        if (returnType == typeof(ValueTask)
            || (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            error = $"'{method.Name}' returns {returnType.Name}. The engine measures Task-returning "
                    + "bodies; wrap it as `() => Method().AsTask()`.";

            return false;
        }

        if (returnType.IsByRefLike)
        {
            error = $"'{method.Name}' returns the by-ref-like type {returnType.Name}, which cannot be "
                    + "carried by a delegate.";

            return false;
        }

        delegateType = typeof(Func<>).MakeGenericType(returnType);
        return true;
    }

}
