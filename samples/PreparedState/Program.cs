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
// Benchmarks are measured in a separate process, so that captured array has to get across a
// boundary. NBenchmark sends it: an int[] is a value whose behaviour under measurement is fully
// determined by its contents, so the worker can be given the bytes and rebuild an equivalent one.
// The capturing form is isolated, and no rewrite is needed.
//
// What it will *not* do is guess. Only a closed set of types is sent - primitives, strings, arrays,
// the standard collections when they carry a default comparer, and types you mark [BenchmarkState].
// A captured open connection, warmed cache or custom comparer is refused by name, because sending
// it would arrive intact and measure differently. That is the one failure a benchmark must not
// have, and probing confirmed it is silent: a fabricated replacement did not throw, it returned
// plausible, wrong numbers.
//
// A refusal is an *error*, not a labelled fallback - in-process measurement is something you ask
// for rather than something that happens to you. Benchmark.RunInProcess is how you ask.
//
// So `prepare` is no longer the difference between isolated and not. It is still the better shape,
// for two reasons this sample shows:
//
//   1. The value is *built* in the measuring process rather than shipped to it - no wire cost, no
//      size ceiling, and nothing reconstructed.
//   2. It runs once per benchmark. A body that mutates its input needs that: `d => Array.Sort(d)`
//      over a captured array sorts an already-sorted array from the second sample onward.
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

// The shape people write first. Correct, and isolated - the array is sent to the worker.
var data = BuildData();
var captured = Benchmark.Run(() => Sum(data), options, "captured");

// A capture that cannot be sent. A Stream's behaviour is not determined by its contents, so it is
// refused by name - and a refusal is now an error, not a labelled fallback. RunInProcess is how you
// say "measure it here anyway": the row is stamped as a request rather than a refusal, and it is
// still never given a ratio against an isolated row.
var handle = new MemoryStream(new byte[4096]);
var unsendable = Benchmark.RunInProcess(() => handle.Length, options, "unsendable");

// The same benchmark, prepared in two delegates. `prepare` runs once, before warmup, in the
// worker - so the cost of building the array is never inside a reading.
var prepared = Benchmark.Run(
    prepare: () => BuildData(),
    body: values => Sum(values),
    options: options,
    name: "prepared");

Report(captured);
Report(prepared);
Report(unsendable);

Console.WriteLine();
Console.WriteLine("=== Suite mode: WithState shares one recipe across the comparison ===");
Console.WriteLine();

// WithState is the suite-shaped version, and this is where it earns its keep rather than merely
// avoiding a fallback: both bodies *mutate* their input. The recipe runs once per benchmark, so each
// one sorts an unsorted array. Capturing a single array instead would isolate perfectly well and
// measure the wrong thing - the second body would sort what the first already sorted, and under the
// default random run order which one that is would change between runs.
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
    "identical cost, the difference between the two configurations was worth roughly 3.3x, which is");
Console.WriteLine(
    "why a capture is sent where it can be rather than quietly costing the run its isolation.");

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
