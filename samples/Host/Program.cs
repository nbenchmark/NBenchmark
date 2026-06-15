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
    // Host mode is isolated by default: this class runs in its own clean-room child
    // process, so JIT, GC, and thread-pool state from other classes can't bias it.
    // Pass --in-process (or call WithIsolation(false)) to run everything in the host.
    [Benchmark]
    public int Compute() => 42;

    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    // Finest granularity: give this one benchmark its very own child process, isolated
    // even from the other benchmarks in this class.
    [Benchmark]
    [IsolatedProcess]
    public int Isolated() => 7;

    // Opt back into the host process for this benchmark only - handy when a benchmark
    // must observe state shared with the host, or when child startup would dominate.
    [Benchmark]
    [InProcess]
    public int InHost() => 13;
}
