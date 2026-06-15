using NBenchmark;
using NBenchmark.Reporters.Console;

var quickOptions = new MeasurementOptions
{
    WarmupIterations = 3,
    Iterations = 40,
    OutlierMode = OutlierMode.IqrFence,
};

// Quick mode (Benchmark.Run) always runs in the current process - it is the fast,
// zero-ceremony path. Reach for an isolated child process when you need a clean-room
// reading: use Suite mode's WithIsolation() (below) or Host mode, which isolates by
// default (see the Host sample).
Console.WriteLine("Quick mode: in-process measurement");

var inProcess = Benchmark.Run(
    () => Thread.SpinWait(5_000),
    quickOptions,
    "quick/in-process");

await inProcess.PrintAsync();

Console.WriteLine();
Console.WriteLine("Suite mode: the whole suite runs in one isolated child process");

// WithIsolation() runs the suite's setup, every benchmark, and its teardown together in
// a single dedicated child process. The parent reads the per-benchmark samples back and
// computes significance and reports exactly as it would for an in-process suite.
await new BenchmarkSuite("isolated-suite")
    .Add("baseline", () => Thread.SpinWait(5_000))
    .Add("candidate", () => Thread.SpinWait(3_500))
    .WithBaseline("baseline")
    .WithWarmup(3)
    .WithIterations(30)
    .WithOutlierMode(OutlierMode.None)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .WithIsolation()
    .RunAsync();
