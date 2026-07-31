using NBenchmark;
using NBenchmark.Reporters.Console;

// ---------------------------------------------------------------------------
// NBenchmark - benchmarking over prepared data
//
// Almost every real benchmark needs input: an array to sort, a document to parse, a buffer to
// hash. The obvious way to write that is to build it first and close over it:
//
//     var data = BuildData();
//     Benchmark.Run(() => Sort(data));            // <- captures `data`
//
// That lambda captures, and a capture cannot be measured in another process. The captured value
// exists in this process and nowhere else, and NBenchmark refuses to fabricate a replacement -
// probing showed a fabricated closure does not throw, it returns plausible, silently wrong
// numbers. So the capturing form is measured here instead, inheriting this process's JIT tiering
// and GC configuration, and labelled 'host'.
//
// The fix is to stop handing over a *value* and hand over a *recipe*. Split the preparation into
// its own delegate and both halves capture nothing, so both can be addressed - the worker follows
// the recipe in the process that does the measuring.
//
// This sample runs the same work both ways and prints where each was measured.
//
// Run with: dotnet run --project samples/PreparedState
// ---------------------------------------------------------------------------

var options = new MeasurementOptions
{
    WarmupIterations = 3,
    Iterations = 40,
};

Console.WriteLine("=== Single mode: the capturing shape, and the prepared-state shape ===");
Console.WriteLine();

// The shape people write first. Correct, and measured in this process.
var data = BuildData();
var captured = Benchmark.Run(() => Sum(data), options, "captured");

// The same benchmark, prepared in two delegates. `prepare` runs once, before warmup, in the
// worker - so the cost of building the array is never inside a reading.
var prepared = Benchmark.Run(
    prepare: () => BuildData(),
    body: values => Sum(values),
    options,
    "prepared");

Report(captured);
Report(prepared);

Console.WriteLine();
Console.WriteLine("=== Suite mode: WithState shares one recipe across the comparison ===");
Console.WriteLine();

// WithState is the suite-shaped version. It matters more here than in Single mode: one worker
// measures the whole suite, so a single capturing body takes every sibling benchmark in-process
// with it. Naming the preparation keeps the entire comparison isolated - and keeps every ratio a
// paired, within-process estimate.
//
// The recipe runs once per benchmark, not once per suite. That is deliberate: two sorts sharing
// one array would have the second measure what the first already sorted, and under the default
// random run order which one that is would change between runs.
var suite = await new BenchmarkSuite("sorting")
    .WithState(() => BuildData())
    .Add("array-sort", values => Array.Sort(values))
    .Add("linq-orderby", values => values.OrderBy(static x => x).ToArray())
    .WithBaseline("array-sort")
    .WithWarmup(3)
    .WithIterations(30)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine();

foreach (var result in suite)
{
    Report(result);
}

Console.WriteLine();
Console.WriteLine(
    "Note: 'host' rows inherit this process's JIT tiering and GC configuration, and are never");
Console.WriteLine(
    "compared against isolated ones - the ratio column shows n/a instead. On bodies of provably");
Console.WriteLine(
    "identical cost, the difference between the two configurations was worth roughly 3.3x.");

static void Report(BenchmarkResult result) => Console.WriteLine(
    $"  {result.Name,-14} {result.Median,9:F1} ns   {result.IsolationStatus} "
    + $"under '{result.RuntimeProfileName}'");

static int[] BuildData() => Enumerable.Range(0, 4096).Reverse().ToArray();

static long Sum(int[] values)
{
    long total = 0;

    foreach (var value in values)
    {
        total += value;
    }

    return total;
}
