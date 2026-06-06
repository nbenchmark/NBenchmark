using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Console;

await BenchmarkHost.Create(args)
    .AddFromAssembly<HostBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress(100, 5))
    .RunAsync();

public class HostBenchmarks
{
    [Benchmark]
    public int Compute() => 42;

    [Benchmark(Baseline = true)]
    public int Baseline() => 1;
}