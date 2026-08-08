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

        return Type.GetType(
            assemblyQualifiedName,
            name => context.LoadFromAssemblyName(name),
            (assembly, name, ignoreCase) => assembly is null
                ? Type.GetType(name, throwOnError: false, ignoreCase)
                : assembly.GetType(name, throwOnError: false, ignoreCase),
            throwOnError: false);
    }
}
