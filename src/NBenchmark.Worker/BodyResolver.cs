using System.Reflection;
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
        out Delegate resolved,
        out string? error)
    {
        resolved = null!;

        if (!TryBindMethod(context, body, out var method, out var receiver, out error))
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

        if (body.StateFactory is not null)
            return TryBindPreparedState(context, created, body, out resolved, out error);

        if (body.Arguments.Count == 0)
        {
            resolved = created;
            return true;
        }

        return TryBindArguments(created, body, out resolved, out error);
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

        return body.Shape != BodyShape.CachedSingleton
               || TryResolveReceiver(ref method, body, out receiver, out error);
    }

    /// <summary>
    ///     Runs the state factory in this process and binds what it produced as the body's argument.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Invoked <b>here</b>, once, before the body is ever measured - which is the entire point.
    ///         The value never crosses the boundary; only the recipe for it does. That is what lets a
    ///         benchmark over a prepared array, an open connection or a warmed cache be isolated at all,
    ///         where serializing the prepared value would either fail or, worse, succeed at producing
    ///         something subtly different.
    ///     </para>
    ///     <para>
    ///         The factory's own exceptions are reported as this benchmark's failure. It is user code
    ///         running before measurement, so a throw here means the benchmark never had valid input -
    ///         which is worth saying plainly rather than surfacing as a dead worker.
    ///     </para>
    /// </remarks>
    private static bool TryBindPreparedState(
        BenchmarkLoadContext context,
        Delegate created,
        BodyRef body,
        out Delegate resolved,
        out string? error)
    {
        resolved = created;
        error = null;

        var parameters = created.Method.GetParameters();

        if (parameters.Length != 1)
        {
            error = $"'{created.Method.Name}' takes {parameters.Length} parameter(s); a body measured "
                    + "over prepared state must take exactly one.";

            return false;
        }

        // The expected type is the body's own parameter type, read from the method resolved here
        // rather than trusted from the plan: both delegates came from metadata tokens, and a
        // disagreement means the address no longer describes the code on disk. FactoryResolver
        // checks it against the factory's declared return type before running any user code.
        if (!FactoryResolver.TryInvoke(
                context,
                body.AssemblyPath,
                body.StateFactory!,
                parameters[0].ParameterType,
                arguments: [],
                out var state,
                out error,
                out _))
        {
            return false;
        }

        if (!ArgumentBinder.TryBind(created, [state], out resolved, out var bindError))
        {
            error = bindError;

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Supplies a parameterized body's argument values, leaving the parameterless delegate the
    ///     engine measures.
    /// </summary>
    /// <remarks>
    ///     The declared parameter types are read from the <b>resolved method</b> rather than trusted
    ///     from the payload, which is the same rule the test-method path follows. A payload's type name
    ///     is a claim about the far side; the method's own signature is the fact. Decoding against the
    ///     claim would let a stale or mismatched request bind a plausible value of the wrong type.
    /// </remarks>
    private static bool TryBindArguments(
        Delegate created,
        BodyRef body,
        out Delegate resolved,
        out string? error)
    {
        resolved = created;
        error = null;

        var parameters = created.Method.GetParameters();

        if (parameters.Length != body.Arguments.Count)
        {
            error = $"'{created.Method.Name}' takes {parameters.Length} parameter(s) but the address "
                    + $"carries {body.Arguments.Count} argument value(s).";

            return false;
        }

        var decoded = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            try
            {
                decoded[i] = TestArgumentCodec.Decode(body.Arguments[i], parameters[i].ParameterType);
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

        if (!ArgumentBinder.TryBind(created, decoded, out resolved, out var bindError))
        {
            error = bindError;
            return false;
        }

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
        ref MethodInfo method,
        BodyRef body,
        out object? receiver,
        out string? error)
    {
        receiver = null;
        error = null;

        var declaringType = method.DeclaringType;

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

            var arguments = new Type[names.Count];

            for (var i = 0; i < names.Count; i++)
            {
                var argument = Type.GetType(names[i], throwOnError: false);

                if (argument is null)
                {
                    error = $"type argument '{names[i]}' could not be resolved in the worker.";
                    return false;
                }

                arguments[i] = argument;
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

        var singleton = BodyRef.FindSingletonField(declaringType);

        if (singleton is null)
        {
            error = $"no cached closure instance was found on '{declaringType.Name}'.";
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
                receiver = Activator.CreateInstance(declaringType, nonPublic: true);
            }
            catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
            {
                error = $"the stateless closure '{declaringType.Name}' could not be constructed: {ex.Message}";
                return false;
            }
        }

        return receiver is not null;
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
            if (body.Arguments.Count == 0 && body.StateFactory is null)
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
