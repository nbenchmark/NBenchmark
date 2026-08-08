using Microsoft.Extensions.DependencyInjection;
using NBenchmark.Attributes;
using NBenchmark.Lifecycle;
using Xunit;

namespace NBenchmark.DependencyInjection.Tests;

public class DependencyInjectionHarnessTests
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
            await BenchmarkHarness.Create([
                    "--filter", "DependentBenchmark.*", "--iterations", "1", "--warmup", "0", "--ops-per-sample", "1", "--launch-count", "1",
                ])
                .AddFromAssembly<DependentBenchmark>()
                .WithServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
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
            await BenchmarkHarness.Create(["--filter", "ScopedDependentBenchmark.*", "--dry-run", "--launch-count", "1"])
                .AddFromAssembly<ScopedDependentBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
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
            await BenchmarkHarness.Create(["--filter", "DisposableBenchmark.*", "--dry-run", "--launch-count", "1"])
                .AddFromAssembly<DisposableBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
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
            await BenchmarkHarness.Create(["--filter", "UnresolvableBenchmark.*", "--dry-run"])
                .AddFromAssembly<UnresolvableBenchmark>()
                .WithScopedServiceProvider(services)
                .WithIsolation(false)
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
            await BenchmarkHarness.Create([
                    "--filter", "DependentBenchmark.*", "--iterations", "1", "--warmup", "0", "--ops-per-sample", "1", "--launch-count", "1",
                ])
                .UseDependencyInjection<DependentBenchmark>(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
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
            await BenchmarkHarness.Create(["--filter", "DisposableBenchmark.*", "--dry-run", "--launch-count", "1"])
                .UseScopedDependencyInjection<DisposableBenchmark>(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
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
            await BenchmarkHarness.Create(["--filter", "ParameterlessBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<ParameterlessBenchmark>()
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
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
            await BenchmarkHarness.Create(["--filter", "ParameterlessBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<ParameterlessBenchmark>()
                .WithInstanceFactory(type =>
                {
                    instantiated = true;
                    return Activator.CreateInstance(type)!;
                })
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        Assert.True(instantiated);
        Assert.True(ParameterlessBenchmark.Invoked);
    }

    [Fact]
    public async Task WithScopedServiceProvider_PerMethod_Disposes_One_Scope_Per_Method()
    {
        // The fix for the unbounded-hook-list leak: each [Benchmark] method gets its
        // own scope, and the scope is disposed when that method's runAsync finishes.
        // Under --dry-run the body is not invoked, so the scope is created but the
        // instance is still resolved and then disposed in the post-run teardown.
        var services = new ServiceCollection()
            .AddSingleton(new DisposableTracker())
            .AddTransient<PerMethodScopeBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "PerMethodScopeBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<PerMethodScopeBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        var tracker = services.GetRequiredService<DisposableTracker>();
        Assert.Equal(PerMethodScopeBenchmark.MethodCount, tracker.DisposeCount);
    }

    [Fact]
    public async Task WithScopedServiceProvider_Leaves_No_Hooks_Bound_To_Host()
    {
        // Regression: the previous design accumulated a closure in a host-level
        // list every time the factory was called. For a suite with N methods the
        // list grew to N entries. With the InstanceHandle model each method's
        // scope is wired into the handle the envelope returns, so the host holds
        // zero per-call references. We assert this by running a run that should
        // not retain scope closures - validated by the disposal count matching
        // the method count rather than a runaway total.
        var services = new ServiceCollection()
            .AddSingleton(new DisposableTracker())
            .AddTransient<PerMethodScopeBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "PerMethodScopeBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<PerMethodScopeBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        var tracker = services.GetRequiredService<DisposableTracker>();
        Assert.True(tracker.DisposeCount > 0, "Scope should have been disposed at least once.");
        Assert.Equal(PerMethodScopeBenchmark.MethodCount, tracker.DisposeCount);
    }

    /// <summary>
    ///     PerClass plus scoped DI resolves to a scope per method, and the attribute does not stop it.
    /// </summary>
    /// <remarks>
    ///     This assertion used to read <c>Assert.Equal(1, ...)</c>, and that was the defect rather
    ///     than the contract: one scope for the whole class means every method after the first reads
    ///     a container - and, in the case the package exists for, a <c>DbContext</c> with a warm
    ///     change tracker - that the previous method left behind. The significance test then compares
    ///     two samples it assumes are independent. The lifetime rule and the sharing it produces are
    ///     the same fact, so this is the test that says which one the engine believes.
    /// </remarks>
    [Fact]
    public async Task WithScopedServiceProvider_PerClass_Scopes_Per_Method()
    {
        var services = new ServiceCollection()
            .AddSingleton(new DisposableTracker())
            .AddTransient<PerClassScopeBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "PerClassScopeBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<PerClassScopeBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        var tracker = services.GetRequiredService<DisposableTracker>();
        Assert.Equal(PerClassScopeBenchmark.MethodCount, tracker.DisposeCount);
    }

    [Fact]
    public async Task WithScopedServiceProvider_And_WithInstanceLifetime_PerClass_Scopes_Per_Method()
    {
        var services = new ServiceCollection()
            .AddSingleton(new DisposableTracker())
            .AddTransient<HarnessPerClassScopeBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "HarnessPerClassScopeBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<HarnessPerClassScopeBenchmark>()
                .WithScopedServiceProvider(services)
                .WithInstanceLifetime(InstanceLifetime.PerClass)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        var tracker = services.GetRequiredService<DisposableTracker>();
        Assert.Equal(HarnessPerClassScopeBenchmark.MethodCount, tracker.DisposeCount);
    }

    /// <summary>
    ///     A class that resets itself keeps PerClass - one scope for the whole class, as asked for.
    /// </summary>
    [Fact]
    public async Task WithScopedServiceProvider_PerClass_With_IStateReset_Keeps_One_Scope()
    {
        var services = new ServiceCollection()
            .AddSingleton(new DisposableTracker())
            .AddTransient<ResettingPerClassScopeBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "ResettingPerClassScopeBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<ResettingPerClassScopeBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        var tracker = services.GetRequiredService<DisposableTracker>();
        Assert.Equal(1, tracker.DisposeCount);
        Assert.True(ResettingPerClassScopeBenchmark.ResetCount > 0, "ResetAsync should have fired between methods.");
    }

    /// <summary>
    ///     So does one that declares the carry-over deliberate. The two routes have to be tested
    ///     apart, because before <c>[SharedState]</c> existed they were the same declaration and an
    ///     empty <c>ResetAsync</c> was the only way to say this.
    /// </summary>
    [Fact]
    public async Task WithScopedServiceProvider_PerClass_With_SharedState_Keeps_One_Scope()
    {
        var services = new ServiceCollection()
            .AddSingleton(new DisposableTracker())
            .AddTransient<SharedStatePerClassScopeBenchmark>()
            .BuildServiceProvider();

        await CaptureAndSuppressConsoleOutputAsync(async () =>
        {
            await BenchmarkHarness.Create(["--filter", "SharedStatePerClassScopeBenchmark.*", "--iterations", "1", "--warmup", "0", "--launch-count", "1"])
                .AddFromAssembly<SharedStatePerClassScopeBenchmark>()
                .WithScopedServiceProvider(services)
                .WithRunOrder(RunOrder.Declaration)
                .WithIsolation(false)
                .RunAsync();
        });

        var tracker = services.GetRequiredService<DisposableTracker>();
        Assert.Equal(1, tracker.DisposeCount);
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

public sealed class PerMethodScopeBenchmark : IDisposable
{
    public const int MethodCount = 2;

    private readonly DisposableTracker _tracker;

    public PerMethodScopeBenchmark(DisposableTracker tracker)
    {
        _tracker = tracker;
    }

    public void Dispose() => _tracker.DisposeCount++;

    [Benchmark]
    public int First() => 1;

    [Benchmark]
    public int Second() => 2;
}

[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class PerClassScopeBenchmark : IDisposable
{
    public const int MethodCount = 2;

    private readonly DisposableTracker _tracker;

    public PerClassScopeBenchmark(DisposableTracker tracker)
    {
        _tracker = tracker;
    }

    public void Dispose() => _tracker.DisposeCount++;

    [Benchmark]
    public int First() => 1;

    [Benchmark]
    public int Second() => 2;
}

public sealed class HarnessPerClassScopeBenchmark : IDisposable
{
    public const int MethodCount = 2;

    private readonly DisposableTracker _tracker;

    public HarnessPerClassScopeBenchmark(DisposableTracker tracker)
    {
        _tracker = tracker;
    }

    public void Dispose() => _tracker.DisposeCount++;

    [Benchmark]
    public int First() => 1;

    [Benchmark]
    public int Second() => 2;
}

[InstanceLifetime(InstanceLifetime.PerClass)]
public sealed class ResettingPerClassScopeBenchmark : IDisposable, IStateReset
{
    public static int ResetCount;

    private readonly DisposableTracker _tracker;

    public ResettingPerClassScopeBenchmark(DisposableTracker tracker)
    {
        _tracker = tracker;
    }

    public void Dispose() => _tracker.DisposeCount++;

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref ResetCount);

        return Task.CompletedTask;
    }

    [Benchmark]
    public int First() => 1;

    [Benchmark]
    public int Second() => 2;
}

[InstanceLifetime(InstanceLifetime.PerClass)]
[SharedState]
public sealed class SharedStatePerClassScopeBenchmark : IDisposable
{
    private readonly DisposableTracker _tracker;

    public SharedStatePerClassScopeBenchmark(DisposableTracker tracker)
    {
        _tracker = tracker;
    }

    public void Dispose() => _tracker.DisposeCount++;

    [Benchmark]
    public int First() => 1;

    [Benchmark]
    public int Second() => 2;
}
