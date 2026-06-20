using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

// MultiLaunch runs each benchmark multiple times as independent launches.
// Each launch includes its own warmup and GC cycle, so variance across launches
// reflects real run-to-run differences (process state, ASLR, scheduler placement).
//
// When LaunchCount > 1, the console reporter shows an additional "Launch Aggregation"
// table with cross-launch statistics. The primary result uses the best launch
// (lowest median), so the main table shows the most favourable reading.
//
// Run with: dotnet run --project samples/MultiLaunch
// Run with: dotnet run --project samples/MultiLaunch -- --launch-count 5

// ── Suite mode with 3 launches ──────────────────────────────────────────────────────
// Each benchmark (sleep100, sleep200) runs 3 times as independent launches.
// Cross-launch aggregation appears in the output below the main comparison table.

await new BenchmarkSuite("sleep")
    .Add("sleep100", () => Task.Delay(1).Wait())
    .Add("sleep200", () => Task.Delay(2).Wait())
    .WithBaseline("sleep100")
    .WithLaunchCount(3)
    .WithWarmup(5)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();
Console.WriteLine(new string('-', 70));
Console.WriteLine();

// ── Host mode with attribute-based LaunchCount ───────────────────────────────────────
// The [Benchmark(LaunchCount = 3)] attribute overrides the default per-method.
// Pass --launch-count 5 on the CLI to override all methods to 5 instead.

await BenchmarkHost.Create(args)
    .AddFromAssembly<CpuBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class CpuBenchmarks
{
    [Benchmark(Baseline = true)]
    public int Baseline() => 42;

    // 3 independent launches for this method only
    [Benchmark(LaunchCount = 3)]
    public int Slow() => Compute(1000);

    // Default launch count (1) - no aggregation
    [Benchmark]
    public int Fast() => Compute(100);

    private static int Compute(int bound)
    {
        var sum = 0;

        for (var i = 0; i < bound; i++)
        {
            sum += i;
        }

        return sum;
    }
}
