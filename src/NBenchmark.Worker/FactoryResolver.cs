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

        if (!TryInvoke(context, targetAssemblyPath, factory, typeof(T), [], out var value, out error, out detail))
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
    /// <param name="arguments">
    ///     Values for the factory's own parameters, in declaration order. Empty for the parameterless
    ///     factories that are the common case; an instance factory is <c>Func&lt;Type, object&gt;</c>
    ///     and is handed the benchmark class here.
    /// </param>
    public static bool TryInvoke(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        Type expected,
        object?[] arguments,
        out object? produced,
        out string? error,
        out string? detail)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(arguments);

        produced = null;
        error = null;
        detail = null;

        if (!TryBind(context, targetAssemblyPath, factory, expected, arguments.Length, out var invoke, out error))
            return false;

        try
        {
            produced = invoke(arguments);
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
    ///     Resolves an address to an invocable method, by whichever of the two addressing modes the
    ///     coordinator chose, <b>without running it</b>.
    /// </summary>
    /// <remarks>
    ///     Public so a caller that will invoke the factory repeatedly - an instance factory runs once
    ///     per benchmark instance - can pay for resolution once, and can establish up front that the
    ///     address is usable. Checking that by invoking would mean building an object nobody asked for
    ///     and then telling a real failure apart from a probe's by reading the message.
    /// </remarks>
    public static bool TryBind(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        Type expected,
        int arity,
        out Func<object?[], object?> invoke,
        out string? error)
    {
        invoke = null!;

        if (!factory.IsWellFormed(out error))
            return false;

        MethodInfo method;
        object? receiver = null;

        if (factory.IsByName)
        {
            if (!TryResolveByName(context, targetAssemblyPath, factory, expected, arity, out method, out error))
                return false;
        }
        // No receivers: a factory that closes over state is refused on the coordinator's side, so one
        // reaching here can only have a static or stateless-closure receiver.
        else if (!BodyResolver.TryBindMethod(
                     context, factory.Body!, ResolvedReceivers.None, out method, out receiver, out var bindError))
        {
            error = $"{factory.Role} could not be resolved because {bindError}";

            return false;
        }

        // Checked before invoking, so a shape mismatch costs nothing and cannot half-run user code.
        // The resolved method's own signature is the fact here; a type name on the wire would only be
        // a claim about the far side.
        if (!expected.IsAssignableFrom(method.ReturnType))
        {
            error = $"{factory.Role} returns {method.ReturnType.Name} rather than {expected.Name}.";

            return false;
        }

        if (method.GetParameters().Length != arity)
        {
            error = $"{factory.Role} takes {method.GetParameters().Length} parameter(s); "
                    + $"{arity} were supplied for it.";

            return false;
        }

        var bound = method;
        var target = receiver;

        // MethodInfo.Invoke rather than a delegate: a factory runs once per instance, never in a
        // measured loop, so there is nothing for the delegate's speed to buy - and building one would
        // mean reconstructing the exact Func<> shape for an arity the caller already knows.
        invoke = args => bound.Invoke(target, args.Length == 0 ? null : args);

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
        int arity,
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

        // Selected by name and arity rather than by an exact parameter-type list, because the caller
        // knows how many arguments it will supply but not what the far build declares them as - and
        // the return-type check the caller then applies is the one that matters.
        var found = type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name == factory.MethodName && m.GetParameters().Length == arity)
            .ToList();

        if (found.Count == 0)
        {
            var shape = arity == 0 ? "parameterless" : $"{arity}-parameter";

            error = $"{factory.Role} could not be located: '{factory.DeclaringTypeFullName}' has no "
                    + $"static {shape} method named '{factory.MethodName}'.";

            return false;
        }

        if (found.Count > 1)
        {
            // Refused rather than resolved by declaration order. Two overloads of the same arity are
            // two different methods, and picking one would measure whichever the reflection order
            // happened to return - a choice that could change between builds.
            error = $"{factory.Role} is ambiguous: '{factory.DeclaringTypeFullName}' declares "
                    + $"{found.Count} static methods named '{factory.MethodName}' taking {arity} "
                    + "parameter(s).";

            return false;
        }

        method = found[0];

        return true;
    }
}
