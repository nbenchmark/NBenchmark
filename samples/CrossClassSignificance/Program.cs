using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

// By default, Host mode computes significance per class: each class gets its own
// baseline, and the console reporter renders one comparison table per class.
// This means benchmarks in StringBenchmarks_Legacy cannot be compared against
// benchmarks in StringBenchmarks_Optimized with a Sig column.
//
// Pass --cross-class on the CLI or call WithCrossClassSignificance() in code to
// compute significance across all classes in a single comparison table. The
// baseline is chosen from the whole group, and the reporter adds a Class column
// so rows can be distinguished.
//
// Try running this sample with and without --cross-class to see the difference:
//
//   dotnet run --project samples/CrossClassSignificance
//   dotnet run --project samples/CrossClassSignificance -- --cross-class

await BenchmarkHost.Create(args)
    .AddFromAssembly(typeof(CrossClassSignificance.StringBenchmarks_Legacy).Assembly)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

namespace CrossClassSignificance
{
    // Legacy implementation using string concatenation.
    // Each call creates multiple intermediate strings, allocating more memory.
    public class StringBenchmarks_Legacy
    {
        [Benchmark(Baseline = true)]
        public string Concat_Small()
        {
            var s = "";
            for (var i = 0; i < 5; i++)
                s += "x";
            return s;
        }

        [Benchmark]
        public string Concat_Large()
        {
            var s = "";
            for (var i = 0; i < 50; i++)
                s += "x";
            return s;
        }
    }

    // Optimized implementation using StringBuilder.
    // Single buffer, fewer allocations, faster for larger strings.
    public class StringBenchmarks_Optimized
    {
        [Benchmark]
        public string Builder_Small()
        {
            var sb = new System.Text.StringBuilder(5);
            for (var i = 0; i < 5; i++)
                sb.Append('x');
            return sb.ToString();
        }

        [Benchmark]
        public string Builder_Large()
        {
            var sb = new System.Text.StringBuilder(50);
            for (var i = 0; i < 50; i++)
                sb.Append('x');
            return sb.ToString();
        }
    }
}
