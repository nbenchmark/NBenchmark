using Microsoft.Extensions.DependencyInjection;

namespace NBenchmark.Extensions.DependencyInjection;

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

        var scope = serviceProvider.CreateScope();

        host.WithInstanceFactory(type => scope.ServiceProvider.GetRequiredService(type));
        host.PostSuiteCleanup = scope.Dispose;

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
