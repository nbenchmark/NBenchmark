using NBenchmark;
using NBenchmark.Reporters.Console;

// MultiRuntimeSuite demonstrates running the same benchmarks across multiple .NET
// runtimes (net8.0, net9.0, net10.0) and comparing results side-by-side.
//
// The project must target all runtimes you want to compare. The .csproj uses
// <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks> so dotnet build -f <tfm>
// can produce output for each runtime.
//
// WithRuntimes implicitly enables process isolation: each runtime runs in a freshly
// spawned child process via dotnet exec, so JIT, GC, and thread-pool state from one
// runtime cannot bias another.
//
// Run with: dotnet run --project samples/MultiRuntimeSuite
//
// The console output shows a "Runtime" column grouping results by target framework.
// The first runtime in the list (Net8) is the implicit baseline for ratio calculations.

var results = await new BenchmarkSuite("string-concat")
    .Add("concat", () => "a" + "b" + "c" + "d" + "e")
    .Add("interpolate", () => $"a {"b"} {"c"} {"d"} {"e"}")
    .Add("join", () => string.Join("", "a", "b", "c", "d", "e"))
    .WithBaseline("concat")
    .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)
    .WithWarmup(3)
    .WithIterations(50)
    .WithReporter(new ConsoleReporter())
    .WithProgress(new ConsoleBenchmarkProgress())
    .RunAsync();
