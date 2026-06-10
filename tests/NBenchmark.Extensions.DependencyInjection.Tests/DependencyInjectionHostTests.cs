using Microsoft.Extensions.DependencyInjection;
using NBenchmark.Attributes;
using Xunit;

namespace NBenchmark.Extensions.DependencyInjection.Tests;

public class DependencyInjectionHostTests
{
    [Fact]
    public async Task WithServiceProvider_Resolves_Benchmark_With_Constructor_Dependencies()
    {
        var store = new RecordingDataStore();

        var services = new ServiceCollection()
            .AddSingleton<IDataStore>(store)
            .AddTransient<DependentBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "DependentBenchmark.*", "--iterations", "1", "--warmup", "0"])
                .AddFromAssembly<DependentBenchmark>()
                .WithServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task WithScopedServiceProvider_Creates_New_Scope_Per_Suite()
    {
        var services = new ServiceCollection()
            .AddSingleton(new ScopeCounter())
            .AddScoped<ScopedDataStore>()
            .AddTransient<ScopedDependentBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "ScopedDependentBenchmark.*", "--dry-run"])
                .AddFromAssembly<ScopedDependentBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        var counter = services.GetRequiredService<ScopeCounter>();
        Assert.Equal(1, counter.ConstructionCount);
    }

    [Fact]
    public async Task WithScopedServiceProvider_Disposes_Scope_After_Teardown()
    {
        var disposable = new DisposableTracker();

        var services = new ServiceCollection()
            .AddSingleton(disposable)
            .AddTransient<DisposableBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "DisposableBenchmark.*", "--dry-run"])
                .AddFromAssembly<DisposableBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        Assert.Equal(1, disposable.DisposeCount);
    }

    [Fact]
    public async Task ScopedProvider_Self_Cleans_When_Factory_Throws()
    {
        var services = new ServiceCollection()
            .AddSingleton<DisposableTracker>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "UnresolvableBenchmark.*", "--dry-run"])
                .AddFromAssembly<UnresolvableBenchmark>()
                .WithScopedServiceProvider(services)
                .RunAsync();
        });
    }

    [Fact]
    public async Task UseDependencyInjection_Discovers_And_Wires_Service_Provider_In_One_Call()
    {
        var store = new RecordingDataStore();

        var services = new ServiceCollection()
            .AddSingleton<IDataStore>(store)
            .AddTransient<DependentBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "DependentBenchmark.*", "--iterations", "1", "--warmup", "0"])
                .UseDependencyInjection<DependentBenchmark>(services)
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        Assert.Equal(1, store.ReadCount);
    }

    [Fact]
    public async Task UseScopedDependencyInjection_Discovers_And_Wires_Scoped_Provider_In_One_Call()
    {
        var tracker = new DisposableTracker();

        var services = new ServiceCollection()
            .AddSingleton(tracker)
            .AddTransient<DisposableBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "DisposableBenchmark.*", "--dry-run"])
                .UseScopedDependencyInjection<DisposableBenchmark>(services)
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        Assert.Equal(1, tracker.DisposeCount);
    }

    [Fact]
    public async Task Without_Service_Provider_Activator_CreateInstance_Still_Works()
    {
        ParameterlessBenchmark.Invoked = false;

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "ParameterlessBenchmark.*", "--iterations", "1", "--warmup", "0"])
                .AddFromAssembly<ParameterlessBenchmark>()
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        Assert.True(ParameterlessBenchmark.Invoked);
    }

    [Fact]
    public async Task WithInstanceFactory_Accepts_Custom_Factory_Without_DI()
    {
        var instantiated = false;

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHost.Create(["--filter", "ParameterlessBenchmark.*", "--iterations", "1", "--warmup", "0"])
                .AddFromAssembly<ParameterlessBenchmark>()
                .WithInstanceFactory(type =>
                {
                    instantiated = true;
                    return Activator.CreateInstance(type)!;
                })
                .WithRunOrder(RunOrder.Declaration)
                .RunAsync();
        });

        Assert.True(instantiated);
        Assert.True(ParameterlessBenchmark.Invoked);
    }

    private static async Task CaptureAndSuppressConsoleOutputAsync(Func<Task> action)
    {
        var sw = new StringWriter();
        var original = Console.Out;
        Console.SetOut(sw);

        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}

public interface IDataStore
{
    public int ReadCount { get; }
    public void Read();
}

public sealed class RecordingDataStore : IDataStore
{
    public int ReadCount { get; private set; }
    public void Read() => ReadCount++;
}

public sealed class DependentBenchmark(IDataStore store)
{
    [Benchmark]
    public int UseDependency()
    {
        store.Read();
        return 1;
    }
}

public sealed class ScopedDataStore
{
    private readonly ScopeCounter _counter;

    public ScopedDataStore(ScopeCounter counter)
    {
        _counter = counter;
        _counter.ConstructionCount++;
    }

    public string GetValue() => "value";
}

public sealed class ScopeCounter
{
    public int ConstructionCount;
}

public sealed class ScopedDependentBenchmark(ScopedDataStore store)
{
    [Benchmark]
    public string UseScopedDependency() => store.GetValue();
}

public sealed class DisposableTracker
{
    public int DisposeCount;
}

public sealed class DisposableBenchmark : IDisposable
{
    private readonly DisposableTracker _tracker;

    public DisposableBenchmark(DisposableTracker tracker)
    {
        _tracker = tracker;
    }

    public void Dispose() => _tracker.DisposeCount++;

    [Benchmark]
    public int DoWork() => 42;
}

public sealed class UnresolvableBenchmark(IMissingDependency missing)
{
    [Benchmark]
    public int Run() => missing is null ? 0 : 1;
}

public interface IMissingDependency
{
}

public sealed class ParameterlessBenchmark
{
    public static bool Invoked;

    [Benchmark]
    public int Run()
    {
        Invoked = true;
        return 1;
    }
}
