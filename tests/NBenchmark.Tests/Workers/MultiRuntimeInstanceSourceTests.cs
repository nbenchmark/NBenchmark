using NBenchmark;
using NBenchmark.Engine;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     W-33: a multi-runtime run refuses an instance source it cannot reproduce, rather than
///     measuring the class without it.
/// </summary>
/// <remarks>
///     <para>
///         Every other mode answers this through <c>WorkerRunPlan.ForDiscoveredClass</c> and declines
///         to isolate. The multi-runtime path never called it - there is nothing for it to decline
///         <i>into</i>, since the whole point is to measure another framework's build - so an
///         unaddressable source simply arrived at the worker as <c>null</c> and the worker fell back
///         to <c>Activator.CreateInstance</c>. For a DI-only class that is a clean instantiation
///         failure. For a class that happens to have a parameterless constructor it is the silent
///         substitution the design refuses everywhere else: every dependency unwired, and the row
///         reported under its own name as though nothing had changed.
///     </para>
///     <para>
///         Refused before the builds, not after: a cross-runtime run shells out to <c>dotnet
///         build</c> once per target framework, and there is no reason to pay for that to discover
///         something known from the harness's own configuration.
///     </para>
/// </remarks>
[Collection(nameof(ConsoleCaptureCollection))]
public class MultiRuntimeInstanceSourceTests
{
    [Fact]
    public async Task A_Live_InstanceSource_Refuses_The_Run_Before_Anything_Is_Built()
    {
        // Requested through the CLI rather than an attribute: a [Runtimes] class sitting in this test
        // assembly would union into every other test's runtime aggregation and start shelling out to
        // `dotnet build` from unrelated runs.
        var harness = BenchmarkHarness.Create([
            "--runtimes", "net8.0",
            "--filter", "MultiRuntimeDiBenchmarks.*",
        ]);

        harness.AddFromAssembly(typeof(MultiRuntimeInstanceSourceTests).Assembly)
            .WithInstanceFactory(type => InstanceHandle.NoTeardown(Activator.CreateInstance(type)!));

        var priorExitCode = Environment.ExitCode;
        var priorError = Console.Error;
        var priorOut = Console.Out;
        using var stderr = new StringWriter();
        Console.SetError(stderr);
        Console.SetOut(TextWriter.Null);

        IReadOnlyList<BenchmarkResult> results;

        try
        {
            results = await harness.RunAsync();
        }
        finally
        {
            Console.SetError(priorError);
            Console.SetOut(priorOut);
            Environment.ExitCode = priorExitCode;
        }

        Assert.Empty(results);

        var message = stderr.ToString();

        Assert.Contains("instance factory", message, StringComparison.Ordinal);
        Assert.Contains("no in-process fallback", message, StringComparison.Ordinal);

        // Nothing was built: the orchestrator announces each target framework before shelling out,
        // and that line never appeared.
        Assert.DoesNotContain("Building for runtimes", message, StringComparison.Ordinal);
    }
}

public class MultiRuntimeDiBenchmarks
{
    [Benchmark]
    public void Measure()
    {
    }
}
