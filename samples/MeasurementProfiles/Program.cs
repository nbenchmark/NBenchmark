using NBenchmark;
using NBenchmark.Reporters.Console;

// A benchmark that allocates memory, making the profile difference visible.
static string AllocateAndConcat(int count)
{
    var result = "";

    for (var i = 0; i < count; i++)
    {
        result += i.ToString();
    }

    return result;
}

// Single mode: run the same benchmark under both profiles.
Console.WriteLine("=== Single Mode: Realistic ===");

Benchmark.Run(
    () => AllocateAndConcat(100),
    MeasurementOptions.For(GcBehavior.Natural),
    "string-concat/realistic").Print();

Console.WriteLine("\n=== Single Mode: Independent ===");

Benchmark.Run(
    () => AllocateAndConcat(100),
    MeasurementOptions.For(GcBehavior.PerSampleCollect),
    "string-concat/independent").Print();

// Suite mode: run two separate suites, one per profile.
Console.WriteLine("\n=== Suite Mode: Realistic ===");

await new BenchmarkSuite("string-concat (Realistic)")
    .Add("concat", () => AllocateAndConcat(100))
    .WithWarmupSamples(10)
    .WithSamples(100)
    .WithGcBehavior(GcBehavior.Natural)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

Console.WriteLine("\n=== Suite Mode: Independent ===");

await new BenchmarkSuite("string-concat (Independent)")
    .Add("concat", () => AllocateAndConcat(100))
    .WithWarmupSamples(10)
    .WithSamples(100)
    .WithGcBehavior(GcBehavior.PerSampleCollect)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
