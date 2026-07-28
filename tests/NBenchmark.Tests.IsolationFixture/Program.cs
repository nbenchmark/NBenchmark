using NBenchmark;
using NBenchmark.Attributes;

namespace NBenchmark.Tests.IsolationFixture;

/// <summary>
///     A real benchmark program, built as a real executable, so the isolation tests can spawn
///     a real child process. Every other isolation test in the repo substitutes
///     <c>FakeProcessLauncher</c>, which is why the composite-key defect that silently emptied
///     <c>RawSamples</c> on every isolated Harness result was able to ship: no test had ever
///     exercised the actual launcher end to end.
///     <para>
///         This cannot live in the test assembly. Isolation re-runs the entry assembly, and
///         under <c>dotnet test</c> that is the test host, not a program containing benchmarks.
///     </para>
/// </summary>
public static class Program
{
    public static Task Main(string[] args)
        => BenchmarkHarness.Create(args)
            .AddFromAssembly<IsolationFixtureBenchmarks>()
            .RunAsync();
}

/// <summary>
///     Two benchmarks with a deliberately large, stable cost difference, so a significance
///     test over them yields a non-null p-value and a non-negligible effect size. Both are
///     pure CPU work with no allocation, so nothing here depends on GC timing.
/// </summary>
public class IsolationFixtureBenchmarks
{
    [Benchmark(Baseline = true)]
    public void Fast() => Thread.SpinWait(200);

    [Benchmark]
    public void Slow() => Thread.SpinWait(2_000);
}

/// <summary>
///     The same two benchmarks under <see cref="InstanceLifetime.PerClass" />, which routes the
///     child through <c>RunPerClassHostChildAsync</c> instead of <c>RunPerMethodHostChildAsync</c>.
///     Both wrote the child payload and both had the same sample-key defect, so both need
///     coverage - a test against only the default lifetime leaves half the bug alive.
/// </summary>
[InstanceLifetime(InstanceLifetime.PerClass)]
public class SharedInstanceBenchmarks
{
    [Benchmark(Baseline = true)]
    public void Fast() => Thread.SpinWait(200);

    [Benchmark]
    public void Slow() => Thread.SpinWait(2_000);
}

/// <summary>
///     A benchmark that never returns, so the launcher's timeout and process-tree kill can be
///     tested against a genuinely wedged child rather than a mock.
///     <para>
///         It lives in its own class so it is only ever reached when a test names it explicitly
///         in an <c>IsolatedRunRequest</c>. Any end-to-end run of this fixture must exclude it
///         with <c>--filter IsolationFixtureBenchmarks.*</c>.
///     </para>
/// </summary>
public class HangingBenchmarks
{
    [Benchmark]
    public void Hang() => Thread.Sleep(Timeout.Infinite);
}
