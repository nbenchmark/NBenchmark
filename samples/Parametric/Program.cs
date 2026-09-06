using NBenchmark;
using NBenchmark.Reporters.Console;

// Parametric benchmarks let you declare a method once and run it across many
// argument sets, producing one result row per set.
//
// Use [Arguments(...)] for a short inline list of literal arguments.
// Use [ArgumentsSource(nameof(Source))] for programmatic, named, or generated cases.
//
// In Harness mode each class renders as a single comparison table: parameter values
// become columns. When competing benchmarks share a parameter group the baseline,
// ratio and significance are computed per group; when a single method is swept the
// Ratio column shows each point's scaling factor against the fastest point. Keep
// related benchmarks in the same class so they share a table.
//
// Run with: dotnet run --project samples/Parametric -- --list
// Run with: dotnet run --project samples/Parametric -- --filter "*LinearSearch*"

await BenchmarkHarness.Create(args)
    .AddFromAssembly(typeof(Program).Assembly)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class LinearSearchBenchmarks
{
    // Inline literal cases. Each [Arguments] produces one row.
    [Benchmark(Baseline = true)]
    [Arguments(10)]
    [Arguments(100)]
    [Arguments(1000)]
    public int LinearSearch(int count)
    {
        var data = EmptyData(count);
        return LinearSearch(data, data[^1]);
    }

    private static int[] EmptyData(int count)
    {
        var data = new int[count];

        for (var i = 0; i < count; i++)
        {
            data[i] = i;
        }

        return data;
    }

    private static int LinearSearch(int[] data, int target)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == target)
                return i;
        }

        return -1;
    }
}

public class BinarySearchBenchmarks
{
    // Programmatic source. The tuple element names appear in the report:
    //   BinarySearch(Count=10000, Target="last")
    [Benchmark(Baseline = true)]
    [ArgumentsSource(nameof(BinarySearchCases))]
    public int BinarySearch(int count, string targetLabel)
    {
        var data = SortedData(count);

        var target = targetLabel switch
        {
            "first" => data[0],
            "middle" => data[data.Length / 2],
            "last" => data[^1],
            _ => data[0],
        };

        return Array.BinarySearch(data, target);
    }

    // The source method is parameterless and can be static or instance.
    // It is invoked once at discovery time and must return IEnumerable<ValueTuple<...>>
    // with an arity matching the benchmark method's parameters.
    public static IEnumerable<(int Count, string Target)> BinarySearchCases()
    {
        yield return (100, "first");
        yield return (10000, "middle");
        yield return (100000, "last");
    }

    private static int[] SortedData(int count)
    {
        var data = new int[count];

        for (var i = 0; i < count; i++)
        {
            data[i] = i * 2;
        }

        return data;
    }
}
