using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

// MultiRuntimeHarness demonstrates running attribute-based benchmarks across multiple
// .NET runtimes (net8.0, net9.0, net10.0).
//
// The project must target all runtimes you want to compare. The .csproj uses
// <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks> so dotnet build -f <tfm>
// can produce output for each runtime.
//
// Two ways to specify runtimes:
//
//   1. [Runtimes] attribute on the class (no CLI flag needed):
//        dotnet run --project samples/MultiRuntimeHarness
//
//   2. --runtimes CLI flag (overrides [Runtimes]):
//        dotnet run --project samples/MultiRuntimeHarness -- --runtimes net8,net9,net10
//        dotnet run -- --runtimes net8,net9 --iterations 500 --reporter markdown --output ./results
//
// The host builds the project for each specified runtime, runs the benchmarks in a
// child process under that runtime, and aggregates the results. The console output
// shows a "Runtime" column grouping results by target framework.

await BenchmarkHarness.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

[Runtimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)]
public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c" + "d" + "e";

    [Benchmark]
    public string Interpolate() => $"a {"b"} {"c"} {"d"} {"e"}";

    [Benchmark]
    public string Join() => string.Join("", "a", "b", "c", "d", "e");

    [Benchmark]
    public string Create() => new(['a', 'b', 'c', 'd', 'e']);
}
