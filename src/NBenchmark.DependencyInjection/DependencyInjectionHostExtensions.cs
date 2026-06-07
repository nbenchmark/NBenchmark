using Microsoft.Extensions.DependencyInjection;

namespace NBenchmark.DependencyInjection;

public static class DependencyInjectionHostExtensions
{
    public static BenchmarkHost WithServiceProvider(
        this BenchmarkHost host,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return host.WithInstanceFactory(serviceProvider.GetRequiredService);
    }

    public static BenchmarkHost WithScopedServiceProvider(
        this BenchmarkHost host,
        IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        IServiceScope? scope = null;

        host.WithInstanceFactory(type =>
        {
            var s = serviceProvider.CreateScope();
            try
            {
                var instance = s.ServiceProvider.GetRequiredService(type);
                scope = s;
                return instance;
            }
            catch
            {
                s.Dispose();
                throw;
            }
        });

        host.PostSuiteCleanup = () =>
        {
            scope?.Dispose();
            scope = null;
        };

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
