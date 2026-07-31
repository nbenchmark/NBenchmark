using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.DependencyInjection;
using NBenchmark.Reporters.Console;

// Pass the container as a *factory*, not as a built provider.
//
// A service provider is live code - it holds singletons, open handles and closures - so it cannot
// cross a process boundary. Handing one to the harness therefore costs the run its isolation: the
// benchmarks are measured in this process, under whatever JIT tiering and GC flavour it happens to
// have, and every result is stamped 'host'.
//
// A static factory is different. It is a *recipe* for a container, and a recipe is addressable: the
// measurement worker locates BuildServices by metadata token, runs it in its own process, and
// resolves the benchmark instances from the container it built there. The container is a different
// instance from any built here, and that is the point rather than a caveat - a benchmark resolved
// from a container this process already warmed up is partly measuring that warmth.
var results = await BenchmarkHarness.Create(args)
    .UseDependencyInjection<DependencyInjectionBenchmarks>(BuildServices)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

// Printed so the sample asserts its own fidelity. If this ever reports 'host', the DI path has
// silently stopped being isolated and the numbers are no longer comparable with previous runs.
Console.WriteLine();

foreach (var result in results)
{
    Console.WriteLine($"  {result.Name}: {result.IsolationStatus} under '{result.RuntimeProfileName}'");
}

static IServiceProvider BuildServices() => new ServiceCollection()
    .AddSingleton<IDataStore, InMemoryDataStore>()
    .AddTransient<OrderRepository>()
    .AddTransient<DependencyInjectionBenchmarks>()
    .BuildServiceProvider();

public interface IDataStore
{
    public int Read();
    public void Write(int value);
}

public sealed class InMemoryDataStore : IDataStore
{
    private int _value;
    public int Read() => _value;
    public void Write(int value) => _value = value;
}

public sealed class OrderRepository(IDataStore store)
{
    public int GetCurrent() => store.Read();
    public void Save(int value) => store.Write(value);
}

public sealed class DependencyInjectionBenchmarks(OrderRepository repository)
{
    [Benchmark]
    public int Read() => repository.GetCurrent();

    [Benchmark]
    public int Write()
    {
        repository.Save(42);
        return 42;
    }
}
