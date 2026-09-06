using NBenchmark;
using NBenchmark.Reporters.Console;

// MultiRuntimeSuite runs the same benchmarks across net8.0, net9.0 and net10.0 and compares the
// results side by side.
//
// The project must target every runtime you want to compare. The .csproj uses
// <TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks>, because each one is built and then
// measured in a worker process built for that same framework.
//
// Multi-runtime needs a [BenchmarkPlan] factory rather than an inline suite. Measuring another
// target framework means measuring a *different build* of this code, and an inline suite's bodies
// are located by metadata token - a number that only means anything inside the build that produced
// it. A factory is found by name, which is stable across builds, so each runtime's worker can
// construct the suite from that runtime's own assemblies.
//
// Run with: dotnet run --project samples/MultiRuntimeSuite
//
// The console output groups rows by runtime. The first runtime listed is the implicit baseline.

await BenchmarkSuite.RunPlanAsync(BuildSuite);

[BenchmarkPlan]
static BenchmarkSuite BuildSuite() =>
    new BenchmarkSuite("string-concat")
        .Add("concat", () => "a" + "b" + "c" + "d" + "e")
        .Add("interpolate", () => $"a {"b"} {"c"} {"d"} {"e"}")
        .Add("join", () => string.Join("", "a", "b", "c", "d", "e"))
        .WithBaseline("concat")
        .WithRuntimes(RuntimeMoniker.Net8, RuntimeMoniker.Net9, RuntimeMoniker.Net10)
        .WithWarmupSamples(3)
        .WithSamples(50)
        .WithReporter(new ConsoleReporter())
        .WithProgress(new ConsoleBenchmarkProgress());
