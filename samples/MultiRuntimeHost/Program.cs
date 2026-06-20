using NBenchmark;
using NBenchmark.Attributes;
using NBenchmark.Reporters.Console;

// MultiRuntimeHost demonstrates running attribute-based benchmarks across multiple
// .NET runtimes (net8.0, net9.0, net10.0) via the --runtimes CLI flag.
//
// The project must target all runtimes you want to compare. The .csproj uses
// <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks> so dotnet build -f <tfm>
// can produce output for each runtime.
//
// Run with: dotnet run --project samples/MultiRuntimeHost -- --runtimes net8,net9,net10
//
// The host builds the project for each specified runtime, runs the benchmarks in a
// child process under that runtime, and aggregates the results. The console output
// shows a "Runtime" column grouping results by target framework.
//
// Combine with other CLI flags:
//   dotnet run -- --runtimes net8,net9 --iterations 500 --reporter markdown --output ./results

await BenchmarkHost.Create(args)
    .AddFromAssembly<StringBenchmarks>()
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();

public class StringBenchmarks
{
    [Benchmark(Baseline = true)]
    public string Concat() => "a" + "b" + "c" + "d" + "e";

    [Benchmark]
    public string Interpolate() => $"a {"b"} {"c"} {"d"} {"e"}";

    [Benchmark]
    public string Join() => string.Join("", "a", "b", "c", "d", "e");

    [Benchmark]
    public string Create() => new string(['a', 'b', 'c', 'd', 'e']);
}
