using System.Reflection;
using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     Runs an <see cref="AddressedFactory" /> in this process and hands back what it built.
/// </summary>
/// <remarks>
///     <para>
///         The single enforcement point for the recipe half of the protocol, as
///         <see cref="BodyResolver" /> is for the body half. Every factory the coordinator addresses -
///         prepared state, service provider, outlier detector, significance test, benchmark plan -
///         arrives here, is resolved, is checked against the type the caller expects, and is invoked
///         with its exceptions unwrapped.
///     </para>
///     <para>
///         The four ways this can fail (unresolvable address, a factory that threw, a null return, an
///         object of the wrong type) are phrased once, here, around
///         <see cref="AddressedFactory.Role" />. Previously each call site worded its own, so the same
///         failure read differently depending on which recipe hit it, and two of the five did not
///         distinguish "threw" from "returned the wrong thing" at all.
///     </para>
///     <para>
///         What is deliberately <b>not</b> decided here is what a failure means. A statistical
///         strategy that cannot be rebuilt degrades to the built-in one and the benchmark is still
///         measurable; a service provider that cannot be rebuilt must fault the group, because
///         constructing the benchmark type without its dependencies would measure a different object
///         and report it under the caller's name. Those are genuinely different policies, so this
///         returns a result and lets each caller apply its own.
///     </para>
/// </remarks>
internal static class FactoryResolver
{
    /// <summary>
    ///     Resolves and invokes <paramref name="factory" />, requiring a non-null
    ///     <typeparamref name="T" />.
    /// </summary>
    public static bool TryInvoke<T>(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        out T produced,
        out string? error,
        out string? detail)
        where T : class
    {
        produced = null!;

        if (!TryInvoke(context, targetAssemblyPath, factory, typeof(T), out var value, out error, out detail))
            return false;

        if (value is null)
        {
            error = $"{factory.Role} returned null.";

            return false;
        }

        produced = (T)value;

        return true;
    }

    /// <summary>
    ///     Resolves and invokes <paramref name="factory" />, requiring the result to be assignable to
    ///     <paramref name="expected" />. A <c>null</c> result is permitted - a prepared-state factory
    ///     producing a nullable value is a legitimate benchmark.
    /// </summary>
    /// <param name="expected">
    ///     The type the caller will use the result as. Checked against the resolved method's
    ///     <b>declared return type</b> before invoking, so a mismatch is reported without running user
    ///     code, and against the produced object afterwards, which catches a covariant return.
    /// </param>
    public static bool TryInvoke(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        Type expected,
        out object? produced,
        out string? error,
        out string? detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(expected);

        produced = null;
        error = null;
        detail = null;

        if (!TryResolve(context, targetAssemblyPath, factory, expected, out var invoke, out error))
            return false;

        try
        {
            produced = invoke();
        }
        catch (Exception ex)
        {
            // The factory is user code and can fail for any reason. Unwrapping the reflection wrapper
            // is what makes the message name the user's own exception rather than
            // TargetInvocationException, which says nothing about what went wrong.
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;

            error = $"{factory.Role} threw {inner.GetType().Name}: {inner.Message}";
            detail = inner.ToString();

            return false;
        }

        if (produced is not null && !expected.IsInstanceOfType(produced))
        {
            error = $"{factory.Role} produced {produced.GetType().Name} rather than {expected.Name}.";

            produced = null;

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Turns an address into a nullary invocation, by whichever of the two addressing modes the
    ///     coordinator chose.
    /// </summary>
    private static bool TryResolve(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        Type expected,
        out Func<object?> invoke,
        out string? error)
    {
        invoke = null!;

        if (!factory.IsWellFormed(out error))
            return false;

        if (factory.IsByName)
        {
            if (!TryResolveByName(context, targetAssemblyPath, factory, expected, out var method, out error))
                return false;

            invoke = () => method.Invoke(null, null);

            return true;
        }

        if (!BodyResolver.TryResolve(context, factory.Body!, out var resolved, out var resolveError))
        {
            error = $"{factory.Role} could not be resolved because {resolveError}";

            return false;
        }

        // Checked before invoking, so a shape mismatch costs nothing and cannot half-run user code.
        // BodyResolver has already bound the delegate against the method's real signature, so this is
        // the assembly on disk disagreeing with what the caller expects rather than a claim on the wire.
        if (!expected.IsAssignableFrom(resolved.Method.ReturnType))
        {
            error = $"{factory.Role} returns {resolved.Method.ReturnType.Name} rather than "
                    + $"{expected.Name}.";

            return false;
        }

        invoke = () => resolved.DynamicInvoke();

        return true;
    }

    /// <summary>
    ///     Binds a factory by fully-qualified name from the assembly under test.
    /// </summary>
    /// <remarks>
    ///     The shape checks live here rather than on the coordinator because the assembly here is a
    ///     <i>different build</i>: under another target framework the method genuinely might have a
    ///     different signature, or not exist at all, and saying so precisely is more useful than a
    ///     cast failure inside a measurement.
    /// </remarks>
    private static bool TryResolveByName(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        Type expected,
        out MethodInfo method,
        out string? error)
    {
        method = null!;
        error = null;

        Assembly target;

        try
        {
            target = context.LoadFromAssemblyPath(Path.GetFullPath(targetAssemblyPath));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            error = $"{factory.Role} could not be located: '{Path.GetFileName(targetAssemblyPath)}' "
                    + $"failed to load: {ex.Message}";

            return false;
        }

        if (target.GetType(factory.DeclaringTypeFullName!, throwOnError: false) is not { } type)
        {
            error = $"{factory.Role} could not be located: the type "
                    + $"'{factory.DeclaringTypeFullName}' was not found in "
                    + $"'{Path.GetFileName(targetAssemblyPath)}'.";

            return false;
        }

        var found = type.GetMethod(
            factory.MethodName!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (found is null)
        {
            error = $"{factory.Role} could not be located: '{factory.DeclaringTypeFullName}' has no "
                    + $"static parameterless method named '{factory.MethodName}'.";

            return false;
        }

        if (!expected.IsAssignableFrom(found.ReturnType))
        {
            error = $"{factory.Role} returns {found.ReturnType.Name} rather than {expected.Name}.";

            return false;
        }

        method = found;

        return true;
    }
}
