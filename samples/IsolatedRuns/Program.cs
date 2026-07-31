using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

// Every mode below measures in a dedicated worker process (nbworker), because JIT tiering,
// dynamic PGO, ReadyToRun and GC flavour are fixed when a process starts and can only be chosen
// for a process that has not started yet. Isolation is the delivery mechanism for that choice -
// it is not, on its own, the thing that makes a benchmark accurate.

var singleOptions = new MeasurementOptions
{
    WarmupIterations = 3,
    Iterations = 40,
    OutlierMode = OutlierMode.IqrFence,
};

Console.WriteLine("Single mode: isolated by default");

// The lambda captures nothing, so NBenchmark locates the compiled method in a worker and
// measures it there. The call signature is unchanged, including the synchronous return.
var isolated = Benchmark.Run(() => Thread.SpinWait(5_000), singleOptions, "single/isolated");
await isolated.PrintAsync();

Console.WriteLine();
Console.WriteLine("Single mode: measuring THIS process, on purpose");

// RunInProcess is not a fallback. It is the correct choice when the current process is the
// subject - cold-start cost, or a body that must observe host state. It is stamped
// InProcessRequested and is never compared against an isolated reading.
var here = Benchmark.RunInProcess(() => Thread.SpinWait(5_000), singleOptions, "single/in-process");
await here.PrintAsync();

Console.WriteLine();
Console.WriteLine($"isolated: {isolated.IsolationStatus} under '{isolated.RuntimeProfileName}'");
Console.WriteLine($"in-process: {here.IsolationStatus} under '{here.RuntimeProfileName}'");

Console.WriteLine();
Console.WriteLine("Suite mode: an ordinary inline suite, measured in one worker");

// No factory, no attribute, nothing restructured. Each body is a non-capturing lambda, so
// NBenchmark addresses each compiled method and measures the whole suite in one worker.
//
// One worker for the whole suite is deliberate: every ratio between these benchmarks is then a
// paired, within-process comparison, so the worker's CPU frequency and thermal state cancel out
// of it. Measuring each benchmark in its own process would turn every ratio into a
// between-process contrast and add variance without removing the error that actually matters -
// the runtime configuration, which is per-process and identical either way.
await new BenchmarkSuite("isolated-suite")
    .Add("baseline", () => Thread.SpinWait(5_000))
    .Add("candidate", () => Thread.SpinWait(3_500))
    .WithBaseline("baseline")
    .WithWarmup(3)
    .WithIterations(30)
    .WithOutlierMode(OutlierMode.None)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();
Console.WriteLine("Suite mode: a plan, for suites a worker must build itself");

// A [BenchmarkPlan] factory is the escape hatch, not the front door. Reach for it when the suite
// holds things a worker cannot be handed - suite setup/teardown, a custom detector instance, a
// service provider, parameter values. The worker invokes this factory in its own process, so all
// of that is constructed there rather than described to it.
await BenchmarkSuite.RunPlanAsync(BuildStatefulSuite);

[BenchmarkPlan]
static BenchmarkSuite BuildStatefulSuite()
{
    var payload = new byte[4096];

    return new BenchmarkSuite("plan-suite")
        .Add("hash", () => payload.GetHashCode())
        .WithSuiteSetup(() => Random.Shared.NextBytes(payload))
        .WithWarmup(3)
        .WithIterations(30)
        .WithReporter(new ConsoleReporter());
}
