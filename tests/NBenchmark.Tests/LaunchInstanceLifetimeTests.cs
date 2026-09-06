using NBenchmark;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     What a launch boundary is made of on the in-process paths: a new instance, a re-run
///     <c>[GlobalSetup]</c>, and - for a container-resolved class - a new scope.
/// </summary>
/// <remarks>
///     <para>
///         The instance used to be built once, outside the launch loop, and reused by every launch.
///         That quietly emptied the one number the launch count exists to produce:
///         <c>LaunchAggregator</c> derives the reported standard error and margin of error from the
///         spread <i>between</i> launches, and three launches sharing one object, one setup and one DI
///         scope measure the same warmed state three times. The field documents a reproducibility
///         figure; what it carried was the opposite.
///     </para>
///     <para>
///         The isolated path never had the defect - each replicate is its own worker process, so a
///         fresh instance comes free - which is exactly why it went unnoticed on the two paths that
///         build their own.
///     </para>
/// </remarks>
[Collection(nameof(ConsoleCaptureCollection))]
public class LaunchInstanceLifetimeTests
{
    [Fact]
    public async Task PerClass_InProcess_Builds_An_Instance_Per_Launch()
    {
        PerClassLaunchBenchmarks.Reset();

        await RunAsync("PerClassLaunchBenchmarks.*");

        Assert.Equal(3, PerClassLaunchBenchmarks.Constructions);

        // Setup is per instance, so it has to move with it. A launch whose instance is fresh but
        // whose setup is not would still be measuring state the previous launch left behind.
        Assert.Equal(3, PerClassLaunchBenchmarks.Setups);
    }

    [Fact]
    public async Task PerMethod_InProcess_Builds_An_Instance_Per_Launch_Per_Method()
    {
        PerMethodLaunchBenchmarks.Reset();

        await RunAsync("PerMethodLaunchBenchmarks.*");

        // Two methods x three launches. The per-method path had the same shape as the per-class one:
        // creation sat outside the launch loop, so the three launches of each method shared one
        // object.
        Assert.Equal(6, PerMethodLaunchBenchmarks.Constructions);
    }

    /// <summary>
    ///     W-30: nothing is asked of <c>IStateReset</c> at a launch boundary, because there is no
    ///     state left to reset there. It fires N-1 times per launch, between methods, and that is the
    ///     whole contract.
    /// </summary>
    [Fact]
    public async Task Reset_Fires_Between_Methods_Within_Each_Launch_And_Not_Between_Launches()
    {
        ResettingLaunchBenchmarks.Reset();

        await RunAsync("ResettingLaunchBenchmarks.*");

        Assert.Equal(3, ResettingLaunchBenchmarks.Constructions);

        // Three launches, two methods each: one gap inside each launch, none across them. Four would
        // mean a reset was being asked to clean an instance that did not exist yet.
        Assert.Equal(3, ResettingLaunchBenchmarks.Resets);
    }

    private static async Task RunAsync(string filter)
    {
        var harness = BenchmarkHarness.Create([
            "--filter", filter,
            "--in-process",
            "--samples", "1",
            "--warmup-samples", "0",
            "--ops-per-sample", "1",
            "--launch-count", "3",
        ]);

        harness.AddFromAssembly(typeof(LaunchInstanceLifetimeTests).Assembly);

        var stdout = Console.Out;
        Console.SetOut(TextWriter.Null);

        try
        {
            await harness.RunAsync();
        }
        finally
        {
            Console.SetOut(stdout);
        }
    }
}

[InstanceLifetime(InstanceLifetime.PerClass)]
[SharedState]
public class PerClassLaunchBenchmarks
{
    public static int Constructions;
    public static int Setups;

    public PerClassLaunchBenchmarks() => Interlocked.Increment(ref Constructions);

    public static void Reset()
    {
        Constructions = 0;
        Setups = 0;
    }

    [GlobalSetup]
    public void Setup() => Interlocked.Increment(ref Setups);

    [Benchmark]
    public void MethodA()
    {
    }

    [Benchmark]
    public void MethodB()
    {
    }
}

public class PerMethodLaunchBenchmarks
{
    public static int Constructions;

    public PerMethodLaunchBenchmarks() => Interlocked.Increment(ref Constructions);

    public static void Reset() => Constructions = 0;

    [Benchmark]
    public void MethodA()
    {
    }

    [Benchmark]
    public void MethodB()
    {
    }
}

[InstanceLifetime(InstanceLifetime.PerClass)]
public class ResettingLaunchBenchmarks : Lifecycle.IStateReset
{
    public static int Constructions;
    public static int Resets;

    public ResettingLaunchBenchmarks() => Interlocked.Increment(ref Constructions);

    public static void Reset()
    {
        Constructions = 0;
        Resets = 0;
    }

    public Task ResetAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Resets);

        return Task.CompletedTask;
    }

    [Benchmark]
    public void MethodA()
    {
    }

    [Benchmark]
    public void MethodB()
    {
    }
}
