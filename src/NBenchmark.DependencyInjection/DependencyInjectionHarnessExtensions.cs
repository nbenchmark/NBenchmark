using Microsoft.Extensions.DependencyInjection;
using NBenchmark.Engine;

namespace NBenchmark.DependencyInjection;

public static class DependencyInjectionHarnessExtensions
{
    /// <remarks>
    ///     A live provider cannot cross a process boundary, so benchmarks resolved this way are measured
    ///     in the host process and labelled. Pass a <c>Func&lt;IServiceProvider&gt;</c> instead to keep
    ///     isolation - see <see cref="UseDependencyInjection{T}(BenchmarkHarness, Func{IServiceProvider})" />.
    /// </remarks>
    public static BenchmarkHarness WithServiceProvider(
        this BenchmarkHarness harness,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return harness.WithInstanceFactory(type => InstanceHandle.NoTeardown(serviceProvider.GetRequiredService(type)));
    }

    public static BenchmarkHarness WithScopedServiceProvider(
        this BenchmarkHarness harness,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        // Create a scope per benchmark-class instance and bundle its disposal into the
        // returned handle. Harness mode will call that teardown after [BenchmarkTeardown]
        // runs for the instance, which preserves ordering and avoids harness-level hooks.
        harness.WithInstanceFactory(type =>
        {
            var scope = serviceProvider.CreateScope();

            try
            {
                return new InstanceHandle(
                    scope.ServiceProvider.GetRequiredService(type),
                    scope.Dispose);
            }
            catch
            {
                scope.Dispose();
                throw;
            }
        });

        return harness;
    }

    /// <inheritdoc cref="UseDependencyInjection{T}(BenchmarkHarness, Func{IServiceProvider})" />
    public static BenchmarkHarness UseDependencyInjection<T>(
        this BenchmarkHarness harness,
        IServiceProvider services)
        => harness.AddFromAssembly<T>().WithServiceProvider(services);

    /// <summary>
    ///     Discovers benchmarks on <typeparamref name="T" />'s assembly and resolves their instances from
    ///     a container built by <paramref name="services" />, keeping the run isolated.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The overload taking a live <see cref="IServiceProvider" /> cannot be isolated: a container
    ///         is live code holding singletons and open handles, so a worker would have to be handed one
    ///         rather than able to build it. Passing the factory instead lets the worker build an
    ///         equivalent container in the process that measures:
    ///     </para>
    ///     <code>
    ///     await BenchmarkHarness.Create(args)
    ///         .UseDependencyInjection&lt;MyBenchmarks&gt;(BuildServices)
    ///         .RunAsync();
    ///
    ///     static IServiceProvider BuildServices() =&gt; new ServiceCollection()
    ///         .AddSingleton&lt;IDataStore, InMemoryDataStore&gt;()
    ///         .AddTransient&lt;MyBenchmarks&gt;()
    ///         .BuildServiceProvider();
    ///     </code>
    ///     <para>
    ///         The factory must be static and capture nothing, for the same reason a benchmark body must.
    ///     </para>
    /// </remarks>
    public static BenchmarkHarness UseDependencyInjection<T>(
        this BenchmarkHarness harness,
        Func<IServiceProvider> services)
    {
        ArgumentNullException.ThrowIfNull(harness);
        ArgumentNullException.ThrowIfNull(services);

        return harness.AddFromAssembly<T>().WithServiceProvider(services);
    }

    public static BenchmarkHarness UseScopedDependencyInjection<T>(
        this BenchmarkHarness harness,
        IServiceProvider services)
        => harness.AddFromAssembly<T>().WithScopedServiceProvider(services);
}
