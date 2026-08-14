using System.Linq;
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
        ResolvedReceivers receivers,
        out T produced,
        out string? error,
        out string? detail)
        where T : class
    {
        produced = null!;

        if (!TryInvoke(
                context, targetAssemblyPath, factory, receivers, typeof(T), [], out var value, out error, out detail))
        {
            return false;
        }

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
        ResolvedReceivers receivers,
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

        if (!TryBind(
                context, targetAssemblyPath, factory, receivers, expected, arguments.Length,
                out var invoke, out error))
        {
            return false;
        }

        return Invoke(factory, expected, invoke, arguments, out produced, out error, out detail);
    }

    /// <summary>
    ///     Runs a bound factory and checks what it produced.
    /// </summary>
    /// <remarks>
    ///     Shared by both invoke paths so the four failure phrasings stay in one place - which is the
    ///     reason this class exists. The second path differs only in where its arguments came from.
    /// </remarks>
    private static bool Invoke(
        AddressedFactory factory,
        Type expected,
        Func<object?[], object?> invoke,
        object?[] arguments,
        out object? produced,
        out string? error,
        out string? detail)
    {
        produced = null;
        error = null;
        detail = null;

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
    ///     Resolves and invokes a factory whose own argument values arrived encoded on the wire.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The values are decoded against the <b>resolved method's</b> declared parameter types,
    ///         which is why this lives here rather than at the call site: this is the only place that
    ///         has the method. The rule is the same one every other decode in the protocol follows - the
    ///         signature on disk is a fact, and a type name on the wire is only a claim about it.
    ///     </para>
    ///     <para>
    ///         This is what makes a prepare delegate able to take parameters at all:
    ///         <c>prepare: (int size) =&gt; Build(size)</c> with the size sent alongside is a complete
    ///         recipe, with the value named rather than captured. The capturing form
    ///         <c>prepare: () =&gt; Build(size)</c> also crosses now - its capture is transferred through
    ///         the group's receiver table - so the two are alternatives rather than a refusal and its
    ///         remedy.
    ///     </para>
    /// </remarks>
    public static bool TryInvoke(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        ResolvedReceivers receivers,
        Type expected,
        out object? produced,
        out string? error,
        out string? detail)
    {
        ArgumentNullException.ThrowIfNull(factory);

        produced = null;
        detail = null;

        var encoded = factory.Body?.Arguments ?? [];

        if (encoded.Count == 0)
        {
            return TryInvoke(
                context, targetAssemblyPath, factory, receivers, expected, [], out produced, out error, out detail);
        }

        if (!TryBind(
                context, targetAssemblyPath, factory, receivers, expected, encoded.Count,
                out var invoke, out var parameters, out error))
        {
            return false;
        }

        var arguments = new object?[encoded.Count];

        for (var i = 0; i < encoded.Count; i++)
        {
            // Checked the same way BodyResolver checks a body's own slots (A10): a slot is read off a
            // frame that came off a pipe, not constructed by the coordinator's own code, so a claim of
            // "both" or "neither" has to be refused here rather than resolved by whichever branch this
            // switch happens to test first. Before this check, a slot carrying both a value and a
            // recipe silently took the value and never said the recipe was there at all.
            if (!encoded[i].IsWellFormed(out var problem))
            {
                error = $"{factory.Role}'s argument '{parameters[i].Name}' {problem}";

                return false;
            }

            // A recipe's own arguments are values, never nested recipes: the thing a prepare delegate
            // needs is the local the user would otherwise have captured, and anything not encodable as
            // a value has its own prepare delegate one slot up rather than one level down.
            if (encoded[i].Value is not { } value)
            {
                error = $"{factory.Role} carries a nested factory for parameter "
                        + $"'{parameters[i].Name}', which is not something a recipe can take.";

                return false;
            }

            try
            {
                arguments[i] = TestArgumentCodec.Decode(value, parameters[i].ParameterType);
            }
            catch (Exception ex) when (ex is FormatException
                                          or OverflowException
                                          or ArgumentException
                                          or InvalidOperationException)
            {
                error = $"{factory.Role} could not be given its argument '{parameters[i].Name}' as "
                        + $"{parameters[i].ParameterType.Name}: {ex.Message}";

                return false;
            }
        }

        return Invoke(factory, expected, invoke, arguments, out produced, out error, out detail);
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
        ResolvedReceivers receivers,
        Type expected,
        int arity,
        out Func<object?[], object?> invoke,
        out string? error)
        => TryBind(context, targetAssemblyPath, factory, receivers, expected, arity, out invoke, out _, out error);

    /// <inheritdoc cref="TryBind(BenchmarkLoadContext, string, AddressedFactory, ResolvedReceivers, Type, int, out Func{object?[], object?}, out string?)" />
    /// <param name="parameters">
    ///     The resolved method's own parameters, for a caller that has to decode values against them.
    ///     They are read from the assembly on disk, never from the wire.
    /// </param>
    public static bool TryBind(
        BenchmarkLoadContext context,
        string targetAssemblyPath,
        AddressedFactory factory,
        ResolvedReceivers receivers,
        Type expected,
        int arity,
        out Func<object?[], object?> invoke,
        out ParameterInfo[] parameters,
        out string? error)
    {
        invoke = null!;
        parameters = [];

        if (!factory.IsWellFormed(out error))
            return false;

        MethodInfo method;
        object? receiver = null;

        if (factory.IsByName)
        {
            if (!TryResolveByName(context, targetAssemblyPath, factory, expected, arity, out method, out error))
                return false;
        }
        // The group's receivers, not an empty set. A factory used to be refused on the coordinator's
        // side the moment it closed over anything, on the reasoning that a recipe exists to be
        // independent of the process that sent it - but a captured `int` is a parameter the factory did
        // not get to declare, not a dependency on this process, and anything genuinely live is refused
        // by the faithfulness rule exactly as it is for a body. Passing the group's table is also what
        // makes a prepare delegate and a body closing over the same local share one object rather than
        // rebuild two.
        else if (!BodyResolver.TryBindMethod(
                     context, factory.Body!, receivers, out method, out receiver, out var bindError))
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

        parameters = method.GetParameters();

        if (parameters.Length != arity)
        {
            error = $"{factory.Role} takes {parameters.Length} parameter(s); "
                    + $"{arity} were supplied for it.";

            parameters = [];

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

        if (ResolveDeclaringType(context, target, targetAssemblyPath, factory.DeclaringTypeFullName!, out var searchedSiblings)
            is not { } type)
        {
            error = $"{factory.Role} could not be located: the type "
                    + $"'{factory.DeclaringTypeFullName}' was not found in "
                    + $"'{Path.GetFileName(targetAssemblyPath)}'"
                    + (searchedSiblings == 0
                        ? "."
                        : $" or the {searchedSiblings} other assembl{(searchedSiblings == 1 ? "y" : "ies")} "
                          + "alongside it.");

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

    /// <summary>
    ///     Searches the target assembly itself, then every other assembly built alongside it - A11: a
    ///     <c>[BenchmarkPlan]</c> factory declared in a shared helper library is exactly the shape a
    ///     multi-runtime suite reaches for, since sharing the plan across the per-runtime projects is
    ///     the point, and by-name addressing exists specifically for multi-runtime.
    ///     <see cref="Assembly.GetType(string, bool)" /> only ever looked at the target itself, so a
    ///     plan factory living in a library like that was never found.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Deliberately not <see cref="Assembly.GetReferencedAssemblies" />: that walks the
    ///         target's own metadata, which only names an assembly the target's <i>compiled code</i>
    ///         uses a type from. A plan factory is never called by the target's own code - it is found
    ///         and invoked by the worker, by reflection - so a shared library holding nothing else the
    ///         target references would never appear there even though the library ships right beside
    ///         it, copied there for exactly this reason. The directory a <c>ProjectReference</c>'s
    ///         output lands in is the fact that is actually true regardless of whether the target's own
    ///         code calls into it.
    ///     </para>
    ///     <para>
    ///         A sibling this fails to load is skipped rather than treated as a refusal of its own -
    ///         the type is not in it either way, and the caller already has a precise message for "not
    ///         found".
    ///     </para>
    /// </remarks>
    private static Type? ResolveDeclaringType(
        BenchmarkLoadContext context, Assembly target, string targetAssemblyPath, string fullName, out int searched)
    {
        if (target.GetType(fullName, throwOnError: false) is { } declaredDirectly)
        {
            searched = 0;

            return declaredDirectly;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetAssemblyPath));
        var targetFileName = Path.GetFileName(targetAssemblyPath);

        var siblings = directory is null
            ? []
            : Directory.EnumerateFiles(directory, "*.dll")
                .Where(path => !string.Equals(Path.GetFileName(path), targetFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        searched = siblings.Count;

        foreach (var path in siblings)
        {
            Assembly loaded;

            try
            {
                // Named, then loaded by name - not loaded directly from the path. Going straight
                // through LoadFromAssemblyPath would bind this file's own identity fine but bypass the
                // context's Load override for everything *it* depends on, including NBenchmark itself -
                // which the target has already loaded through that override. Two different routes to
                // the same NBenchmark.dll produce two different Type identities for BenchmarkSuite, and
                // the return-type check two lines below this method would then refuse a plan that is
                // completely well-formed, reporting "returns BenchmarkSuite rather than BenchmarkSuite".
                loaded = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(path));
            }
            catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or FileNotFoundException)
            {
                continue;
            }

            if (loaded.GetType(fullName, throwOnError: false) is { } found)
                return found;
        }

        return null;
    }
}
