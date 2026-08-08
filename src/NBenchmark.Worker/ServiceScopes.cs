using System.Reflection;
using NBenchmark.Engine;

namespace NBenchmark.Worker;

/// <summary>
///     Creates a dependency-injection scope per benchmark instance, without the worker depending on
///     the dependency-injection package.
/// </summary>
/// <remarks>
///     <para>
///         Reached by reflection deliberately. <c>nbworker</c> is a plain console application and
///         core NBenchmark takes no dependency on
///         <c>Microsoft.Extensions.DependencyInjection.Abstractions</c> - that is the whole reason
///         <c>NBenchmark.DependencyInjection</c> is a separate package. Referencing it here to call
///         one extension method would put it in every worker's dependency graph, including the
///         overwhelming majority of runs that use no container at all.
///     </para>
///     <para>
///         The types are resolved through the <b>target's</b> load context, which is where they
///         certainly are: a run reaches this code only because the user called
///         <c>WithScopedServiceProvider</c>, so their benchmark project references the package by
///         construction. This is the same resolution route custom statistical strategies already take.
///     </para>
///     <para>
///         What is reproduced is exactly what <c>IServiceProvider.CreateScope()</c> does - resolve
///         <c>IServiceScopeFactory</c> from the container and call <c>CreateScope</c> - so a scope
///         made here is the one the user's own code would have made.
///     </para>
/// </remarks>
internal static class ServiceScopes
{
    private const string ScopeFactoryTypeName =
        "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory, "
        + "Microsoft.Extensions.DependencyInjection.Abstractions";

    /// <summary>
    ///     Builds a resolver that gives each instance its own scope, disposed with the instance.
    /// </summary>
    public static bool TryCreateScopedResolver(
        BenchmarkLoadContext context,
        IServiceProvider provider,
        out Func<Type, InstanceHandle> resolve,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(provider);

        resolve = null!;
        error = null;

        // The type resolver must use the assembly the assembly-resolver just returned. Ignoring it and
        // calling the plain Type.GetType looks equivalent and is not: it searches the *worker's* default
        // context, which has no container assemblies in it, so every lookup fails and the run reports a
        // container that could not be scoped rather than one that could.
        var scopeFactoryType = Type.GetType(
            ScopeFactoryTypeName,
            name => context.LoadFromAssemblyName(name),
            (assembly, name, ignoreCase) => assembly is null
                ? Type.GetType(name, throwOnError: false, ignoreCase)
                : assembly.GetType(name, throwOnError: false, ignoreCase),
            throwOnError: false);

        if (scopeFactoryType is null)
        {
            error = "IServiceScopeFactory could not be loaded in the worker, so no scope can be "
                    + "created. This means the container's own dependency-injection assemblies are "
                    + "not reachable from the assembly under test.";

            return false;
        }

        if (provider.GetService(scopeFactoryType) is not { } scopeFactory)
        {
            error = "the container built by the factory does not provide IServiceScopeFactory, so it "
                    + "cannot create the per-benchmark scope that scoped registrations need.";

            return false;
        }

        if (scopeFactoryType.GetMethod("CreateScope", Type.EmptyTypes) is not { } createScope)
        {
            error = "IServiceScopeFactory in the worker has no CreateScope() method.";

            return false;
        }

        resolve = type => Resolve(type, scopeFactory, createScope);

        return true;
    }

    private static InstanceHandle Resolve(Type type, object scopeFactory, MethodInfo createScope)
    {
        var scope = createScope.Invoke(scopeFactory, null)
                    ?? throw new InvalidOperationException("IServiceScopeFactory.CreateScope() returned null.");

        try
        {
            var scoped = scope.GetType().GetProperty("ServiceProvider")?.GetValue(scope) as IServiceProvider
                         ?? throw new InvalidOperationException(
                             "The scope produced by IServiceScopeFactory exposes no IServiceProvider.");

            var instance = scoped.GetService(type)
                           ?? throw new InvalidOperationException(
                               $"No service of type '{type.FullName}' is registered in the container built "
                               + "by your factory. The worker builds its own container from that factory, "
                               + "so a registration added outside it is not present.");

            // Disposing the scope is what makes the registration scoped rather than merely resolved
            // through one: it is where a DbContext's connection and change tracker go.
            return new InstanceHandle(instance, () => (scope as IDisposable)?.Dispose());
        }
        catch
        {
            (scope as IDisposable)?.Dispose();

            throw;
        }
    }
}
