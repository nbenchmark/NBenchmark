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
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(body);

        resolved = null!;
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

        MethodInfo method;

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

        object? receiver = null;

        if (body.Shape == BodyShape.CachedSingleton
            && !TryResolveReceiver(ref method, body, out receiver, out error))
            return false;

        if (!TryDelegateType(method, out var delegateType, out error))
            return false;

        try
        {
            resolved = method.CreateDelegate(delegateType, receiver);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or MissingMethodException)
        {
            error = $"the resolved method '{method.Name}' could not be bound as {delegateType.Name}: {ex.Message}";
            return false;
        }
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
    private static bool TryDelegateType(MethodInfo method, out Type delegateType, out string? error)
    {
        delegateType = typeof(Action);
        error = null;

        if (method.GetParameters().Length != 0)
        {
            error = $"'{method.Name}' takes {method.GetParameters().Length} parameter(s); "
                    + "a benchmark body must take none.";

            return false;
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
