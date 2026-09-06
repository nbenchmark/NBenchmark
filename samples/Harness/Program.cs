using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

await BenchmarkHarness.Create(args)
    .AddFromAssembly<HarnessBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class HarnessBenchmarks
{
    // Harness mode is isolated by default: this class runs in its own clean-room child
    // process, so JIT, GC, and thread-pool state from other classes can't bias it.
    // Pass --in-process (or call WithIsolation(Isolation.Off)) to run everything in the harness.
    [Benchmark]
    public int Compute() => 42;

    [Benchmark(Baseline = true)]
    public int Baseline() => 1;

    // Finest granularity: give this one benchmark its very own child process, isolated
    // even from the other benchmarks in this class.
    [Benchmark]
    [Isolation(Isolation.Required)]
    public int Isolated() => 7;

    // Opt back into the harness process for this benchmark only - handy when a benchmark
    // must observe state shared with the harness, or when child startup would dominate.
    [Benchmark]
    [Isolation(Isolation.Off)]
    public int InHarness() => 13;
}
