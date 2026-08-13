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
        catch (Exception ex) when (ex is ArgumentException or MissingMethodException)
        {
            error = $"the resolved method '{method.Name}' could not be bound as {delegateType.Name}: {ex.Message}";
            return false;
        }

        if (body.Arguments.Count == 0)
        {
            resolved = created;
            return true;
        }

        return TryBindArguments(context, created, body, out resolved, out boundArguments, out error);
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
        catch (Exception ex) when (ex is ArgumentException or MissingMethodException)
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
    ///         Separate from <see cref="TryResolve" /> because a factory and a benchmark body need the
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

            // A static method needs no receiver, and every other shape was refused before it could be
            // addressed. Switched rather than tested for one shape, so a new one cannot silently take
            // the null-receiver path - which is how a transferred receiver first arrived bound to
            // nothing and failed inside the measurement rather than at bind time.
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

        foreach (var capture in captures)
        {
            if (!TryResolveField(type, capture, out var field, out error))
                return false;

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
            else if (!TryDecode(capture, field!.FieldType, out value, out error))
            {
                return false;
            }

            try
            {
                field!.SetValue(instance, value);
            }
            catch (Exception ex) when (ex is ArgumentException or FieldAccessException)
            {
                error = $"the captured value for '{capture.FieldName}' could not be assigned: {ex.Message}";
                return false;
            }
        }

        built = instance;

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

    private static bool TryResolveField(
        Type type,
        CapturedField capture,
        out FieldInfo? field,
        out string? error)
    {
        field = null;
        error = null;

        try
        {
            if (type.Module.ResolveField(capture.FieldToken) is not { } resolved)
            {
                error = $"metadata token 0x{capture.FieldToken:X8} is not a field.";
                return false;
            }

            // A generic display class - a lambda declared inside a generic method - resolves its
            // fields against the open type, and those cannot be set on an instance of the closed one.
            field = type.IsConstructedGenericType
                ? FieldInfo.GetFieldFromHandle(resolved.FieldHandle, type.TypeHandle)
                : resolved;
        }
        catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
        {
            error = $"metadata token 0x{capture.FieldToken:X8} could not be resolved as a field: {ex.Message}";
            return false;
        }

        var actual = field!.FieldType.AssemblyQualifiedName ?? field.FieldType.FullName ?? field.FieldType.Name;

        if (!string.Equals(actual, capture.DeclaredTypeName, StringComparison.Ordinal))
        {
            error = $"the field '{field.Name}' is declared '{field.FieldType.Name}' here but the "
                    + $"address carries a value for '{capture.DeclaredTypeName}'.";

            return false;
        }

        return true;
    }

    private static bool TryDecode(CapturedField capture, Type declared, out object? value, out string? error)
    {
        value = null;
        error = null;

        try
        {
            value = capture.Kind switch
            {
                CapturedValueKind.Binary => StateTransfer.FromBytes(declared, capture.Binary ?? []),
                _ => JsonSerializer.Deserialize(capture.Json ?? "null", declared, StateTransfer.SerializerOptions),
            };

            return true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            error = $"the captured value for '{capture.FieldName}' could not be decoded as "
                    + $"'{declared.Name}': {ex.Message}";

            return false;
        }
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
    ///         user's code. Simple mode measures unboxed today and must keep doing so once it runs
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
