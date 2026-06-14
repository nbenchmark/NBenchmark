using NBenchmark;
using NBenchmark.Reporters.Console;

var quickOptions = new MeasurementOptions
{
    WarmupIterations = 3,
    Iterations = 40,
    OutlierMode = OutlierMode.IqrFence,
};

Console.WriteLine("Quick mode: in-process vs isolated");

var inProcess = Benchmark.Run(
    () => Thread.SpinWait(5_000),
    quickOptions,
    "quick/in-process");

var isolatedQuick = Benchmark.RunIsolated(
    () => Thread.SpinWait(5_000),
    quickOptions,
    "quick/isolated");

await inProcess.PrintAsync();
await isolatedQuick.PrintAsync();

Console.WriteLine();
Console.WriteLine("Suite mode: each benchmark runs in its own isolated child process");

await new BenchmarkSuite("isolated-suite")
    .Add("baseline", () => Thread.SpinWait(5_000))
    .Add("candidate", () => Thread.SpinWait(3_500))
    .WithBaseline("baseline")
    .WithWarmup(3)
    .WithIterations(30)
    .WithOutlierMode(OutlierMode.None)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunIsolatedAsync();
