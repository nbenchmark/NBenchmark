using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

// Parametric benchmarks let you declare a method once and run it across many
// argument sets, producing one result row per set.
//
// Use [BenchmarkCase(...)] for a short inline list of literal arguments.
// Use [BenchmarkCases(nameof(Source))] for programmatic, named, or generated cases.
//
// Run with: dotnet run --project samples/Parametric -- --list
// Run with: dotnet run --project samples/Parametric -- --filter "*LinearSearch*"

await BenchmarkHost.Create(args)
    .AddFromAssembly<SearchBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class SearchBenchmarks
{
    // Inline literal cases. Each [BenchmarkCase] produces one row.
    [Benchmark(Baseline = true)]
    [BenchmarkCase(10)]
    [BenchmarkCase(100)]
    [BenchmarkCase(1000)]
    public int LinearSearch(int count)
    {
        var data = EmptyData(count);
        return LinearSearch(data, data[^1]);
    }

    // Programmatic source. The tuple element names appear in the report:
    //   BinarySearch(Count=10000, Target="last")
    [Benchmark]
    [BenchmarkCases(nameof(BinarySearchCases))]
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

    private static int[] EmptyData(int count)
    {
        var data = new int[count];
        for (var i = 0; i < count; i++)
            data[i] = i;
        return data;
    }

    private static int[] SortedData(int count)
    {
        var data = new int[count];
        for (var i = 0; i < count; i++)
            data[i] = i * 2;
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
