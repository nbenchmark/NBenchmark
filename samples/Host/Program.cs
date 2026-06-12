using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

await BenchmarkHost.Create(args)
    .AddFromAssembly<HostBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class HostBenchmarks
{
    [Benchmark]
    public int Compute() => 42;

    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    // Opt this benchmark into a clean-room child process so its measurement is not
    // influenced by JIT, GC, or thread-pool state warmed up by the other benchmarks.
    [Benchmark]
    [IsolatedProcess]
    public int Isolated() => 7;
}
