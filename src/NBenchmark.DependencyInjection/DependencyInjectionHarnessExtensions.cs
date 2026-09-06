using Microsoft.Extensions.DependencyInjection;
using NBenchmark.Engine;
using NBenchmark.Workers;

namespace NBenchmark.DependencyInjection;

public static class DependencyInjectionHarnessExtensions
{
    // No root-resolved overload here. BenchmarkHarness.WithServices is the root-resolved form and
    // needs nothing from this package - Func<IServiceProvider> is a BCL type - so the two live where
    // their dependencies do. An extension method of the same name on the same type would in any case
    // be shadowed by the instance method and never called.

    // No overload taking a built IServiceProvider, on purpose. It set no Recipe, so InstanceSource
    // .Refusal() was unconditional and the run threw before anything was measured - a compile error
    // catches that earlier than a thrown run does. Pass a Func<IServiceProvider> instead.

    /// <summary>
    ///     Resolves benchmark instances from a container built by <paramref name="factory" />, giving
    ///     each instance its own <see cref="IServiceScope" /> - and keeping the run isolated.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         There is no overload taking a built <see cref="IServiceProvider" />: it is live code and
    ///         cannot cross a process boundary, so a scoped-DI benchmark configured that way would be
    ///         permanently measured in the host process - the flagship EF Core case, and the one the
    ///         whole package is usually installed for, with the numbers carrying the host's JIT tiering
    ///         and GC flavour, worth up to 3.3x on bodies of provably identical cost.
    ///     </para>
    ///     <para>
    ///         The worker runs the factory, builds its own container, and creates a scope per benchmark
    ///         instance - so an <c>AddScoped</c> <c>DbContext</c> is resolved from a real scope and
    ///         disposed with the instance, rather than shared from the root. Sharing it is what warms
    ///         one method's change tracker for the next and makes the two timings dependent, which is
    ///         precisely the assumption the significance test rests on.
    ///     </para>
    ///     <code>
    ///     await BenchmarkHarness.Create(args)
    ///         .AddFromAssembly&lt;OrderBenchmarks&gt;()
    ///         .WithScopedServices(BuildServices)
    ///         .RunAsync();
    ///
    ///     static IServiceProvider BuildServices() =&gt; new ServiceCollection()
    ///         .AddDbContext&lt;OrderContext&gt;(o =&gt; o.UseSqlite("Data Source=bench.db"))
    ///         .AddScoped&lt;OrderRepository&gt;()
    ///         .AddTransient&lt;OrderBenchmarks&gt;()
    ///         .BuildServiceProvider();
    ///     </code>
    ///     <para>
    ///         The factory must be static and capture nothing, for the same reason a benchmark body
    ///         must: a factory that captures would have to run here, and what it builds here is the
    ///         live object that cannot cross.
    ///     </para>
    /// </remarks>
    public static BenchmarkHarness WithScopedServices(
        this BenchmarkHarness harness,
        Func<IServiceProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(factory);

        return harness.WithInstanceSource(new InstanceSource
        {
            Kind = InstanceSourceKind.ScopedServiceProvider,
            Recipe = factory,
            Resolve = ScopedResolver(factory),
        });
    }

    /// <summary>
    ///     A host-side resolver that scopes per instance, building the container on first use.
    /// </summary>
    /// <remarks>
    ///     Deferred for the same reason the unscoped path defers: on an isolated run the coordinator
    ///     measures nothing, so building a container here - opening a database, constructing an EF
    ///     model - is pure cost in a process with no benchmark in it.
    /// </remarks>
    private static Func<Type, InstanceHandle> ScopedResolver(Func<IServiceProvider> factory)
    {
        var provider = new Lazy<IServiceProvider>(
            () => factory() ?? throw new BenchmarkConfigurationException(
                "The service provider factory returned null."));

        return type =>
        {
            // The scope's disposal is bundled into the handle, so Harness mode tears it down after
            // [GlobalTeardown] runs for the instance - preserving ordering without harness-level
            // hooks.
            var scope = provider.Value.CreateScope();

            try
            {
                return new InstanceHandle(scope.ServiceProvider.GetRequiredService(type), scope.Dispose);
            }
            catch
            {
                scope.Dispose();

                throw;
            }
        };
    }
}
