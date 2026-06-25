using Microsoft.Extensions.DependencyInjection;
using NBenchmark.Engine;

namespace NBenchmark.DependencyInjection;

public static class DependencyInjectionHarnessExtensions
{
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

    public static BenchmarkHarness UseDependencyInjection<T>(
        this BenchmarkHarness harness,
        IServiceProvider services)
        => harness.AddFromAssembly<T>().WithServiceProvider(services);

    public static BenchmarkHarness UseScopedDependencyInjection<T>(
        this BenchmarkHarness harness,
        IServiceProvider services)
        => harness.AddFromAssembly<T>().WithScopedServiceProvider(services);
}
