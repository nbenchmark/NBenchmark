using Microsoft.Extensions.DependencyInjection;
using NBenchmark.Engine;

namespace NBenchmark.DependencyInjection;

public static class DependencyInjectionHostExtensions
{
    public static BenchmarkHost WithServiceProvider(
        this BenchmarkHost host,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return host.WithInstanceFactory(type => InstanceHandle.NoTeardown(serviceProvider.GetRequiredService(type)));
    }

    public static BenchmarkHost WithScopedServiceProvider(
        this BenchmarkHost host,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        // Create a scope per benchmark-class instance and bundle its disposal into the
        // returned handle. Host mode will call that teardown after [BenchmarkTeardown]
        // runs for the instance, which preserves ordering and avoids host-level hooks.
        host.WithInstanceFactory(type =>
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

        return host;
    }

    public static BenchmarkHost UseDependencyInjection<T>(
        this BenchmarkHost host,
        IServiceProvider services)
        => host.AddFromAssembly<T>().WithServiceProvider(services);

    public static BenchmarkHost UseScopedDependencyInjection<T>(
        this BenchmarkHost host,
        IServiceProvider services)
        => host.AddFromAssembly<T>().WithScopedServiceProvider(services);
}
