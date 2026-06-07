using Microsoft.Extensions.DependencyInjection;
using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Console;
using NBenchmark.DependencyInjection;

var services = new ServiceCollection()
    .AddSingleton<IDataStore, InMemoryDataStore>()
    .AddTransient<OrderRepository>()
    .AddTransient<DependencyInjectionBenchmarks>()
    .BuildServiceProvider();

await BenchmarkHost.Create(args)
    .UseDependencyInjection<DependencyInjectionBenchmarks>(services)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress(100, 5))
    .RunAsync();

public interface IDataStore
{
    int Read();
    void Write(int value);
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
    public int Read()
    {
        return repository.GetCurrent();
    }

    [Benchmark]
    public int Write()
    {
        repository.Save(42);
        return 42;
    }
}
