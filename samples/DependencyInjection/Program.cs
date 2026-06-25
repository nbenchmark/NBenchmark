using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.DependencyInjection;
using NBenchmark.Reporters.Console;

var services = new ServiceCollection()
    .AddSingleton<IDataStore, InMemoryDataStore>()
    .AddTransient<OrderRepository>()
    .AddTransient<DependencyInjectionBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHarness.Create(args)
    .UseDependencyInjection<DependencyInjectionBenchmarks>(services)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

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
