namespace NBenchmark.Worker;

/// <summary>
///     Resolves an assembly-qualified type name in the worker, from the <b>target's</b> dependency
///     graph rather than the worker's own.
/// </summary>
/// <remarks>
///     <para>
///         The plain <see cref="Type.GetType(string)" /> searches this process's default context,
///         which is where nothing belonging to the code under test ever is - the target is loaded into
///         a <see cref="BenchmarkLoadContext" /> so its own transitive references resolve. A lookup
///         against the default context therefore fails for a type that is certainly present, and the
///         failure is silent wherever the caller has a fallback.
///     </para>
///     <para>
///         The two-delegate overload of <c>Type.GetType</c> is what threads the context in, and the
///         second delegate must resolve <i>from the assembly the first one returned</i>. Discarding it
///         and calling the plain lookup looks equivalent and is not; that mistake shipped once already,
///         in the strategy loader, and cost every named custom detector its registration.
///     </para>
///     <para>
///         <b>Returns <c>null</c> rather than throwing</b>, which the signature says and the
///         implementation did not. <c>throwOnError: false</c> governs the parser's own not-found path
///         and nothing else: an exception out of the <i>assembly resolver</i> propagates, and
///         <c>LoadFromAssemblyName</c> throws <see cref="FileNotFoundException" /> for anything the
///         target's graph cannot supply. Every caller here treats <c>null</c> as "unresolved" and has a
///         precise message ready for it, so the throw skipped all of them and unwound to the group
///         handler - which reports a missing shared framework, and sent the reader after a file that
///         was not the problem. One unresolvable type argument cost the whole group and got the wrong
///         advice.
///     </para>
/// </remarks>
internal static class TypeNames
{
    public static Type? Resolve(string assemblyQualifiedName, BenchmarkLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The plain lookup first: a type argument from the framework or from NBenchmark itself is in
        // the default context, and the common case stays free of load-context subtleties.
        if (Type.GetType(assemblyQualifiedName, throwOnError: false) is { } fromDefault)
            return fromDefault;

        try
        {
            return Type.GetType(
                assemblyQualifiedName,
                name => context.LoadFromAssemblyName(name),
                (assembly, name, ignoreCase) => assembly is null
                    ? Type.GetType(name, throwOnError: false, ignoreCase)
                    : assembly.GetType(name, throwOnError: false, ignoreCase),
                throwOnError: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                       or FileLoadException
                                       or BadImageFormatException
                                       or TypeLoadException)
        {
            // Not resolvable here, which is exactly what this method's callers are asking. Caught
            // around the whole lookup rather than inside the resolver delegate, because returning null
            // from the resolver only sends Type.GetType back to the default context - where nothing
            // belonging to the code under test ever is.
            return null;
        }
    }
}
