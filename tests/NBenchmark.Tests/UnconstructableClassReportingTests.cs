using NBenchmark.Attributes;
using NBenchmark.Engine;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests;

/// <summary>
///     A benchmark class the harness cannot construct appears in the report as errored rows, rather
///     than shortening the table.
/// </summary>
/// <remarks>
///     The isolated path has always synthesised rows for this case
///     (<c>WorkerGroupRunner.ToErroredResults</c>). The in-process per-class path returned early
///     instead, so the class simply went missing - and a shorter table is the one failure shape a
///     reader has no way to notice. There is no gap to see, no error to read, and the only trace was
///     a line on stdout that every file reporter drops.
/// </remarks>
public class UnconstructableClassReportingTests
{
    private static BenchmarkHarness Harness(string category)
    {
        var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

        return harness
            .AddFromAssembly(typeof(UnconstructableClassReportingTests).Assembly)
            .WithCategoryFilter([category])
            .WithLaunchCount(1)
            .WithIsolation(false);
    }

    /// <summary>
    ///     PerClass: one instance is created for the whole class, so the failure is the class's and
    ///     every method in it must still be accounted for.
    /// </summary>
    [Fact]
    public async Task PerClass_Unconstructable_Reports_One_Errored_Row_Per_Benchmark()
    {
        var results = await Harness("unconstructable-perclass").RunAsync();

        var rows = results.Where(r => r.ClassName == nameof(UnconstructablePerClassBenchmarks)).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Errored, $"'{r.Name}' should be errored"));

        // The reason, not a placeholder. It used to go only to stdout, where no reporter sees it.
        Assert.All(rows, r => Assert.Contains("parameterless constructor", r.ErrorMessage ?? ""));
        Assert.All(rows, r => Assert.Contains(nameof(UnconstructablePerClassBenchmarks), r.ErrorMessage ?? ""));
    }

    /// <summary>
    ///     PerMethod already reported this. Pinned so the two paths cannot drift apart again - they
    ///     had already diverged once, which is how one of them came to drop the class silently.
    /// </summary>
    [Fact]
    public async Task PerMethod_Unconstructable_Reports_One_Errored_Row_Per_Benchmark()
    {
        var results = await Harness("unconstructable-permethod").RunAsync();

        var rows = results.Where(r => r.ClassName == nameof(UnconstructablePerMethodBenchmarks)).ToList();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.Errored, $"'{r.Name}' should be errored"));
        Assert.All(rows, r => Assert.Contains("parameterless constructor", r.ErrorMessage ?? ""));
    }

    /// <summary>
    ///     An errored row is not an isolation offender, so a class that could not be constructed does
    ///     not also get reported as having been measured under the host's configuration.
    /// </summary>
    [Fact]
    public async Task Unconstructable_Rows_Are_Not_Isolation_Offenders()
    {
        var results = await Harness("unconstructable-perclass").RunAsync();

        var rows = results.Where(r => r.ClassName == nameof(UnconstructablePerClassBenchmarks)).ToList();

        using var error = new StringWriter();

        Assert.True(IsolationAudit.Enforce(rows, error));
        Assert.Equal("", error.ToString());
    }
}

/// <summary>No parameterless constructor, and no instance factory configured to supply one.</summary>
[BenchmarkCategory("unconstructable-perclass")]
[InstanceLifetime(InstanceLifetime.PerClass)]
public class UnconstructablePerClassBenchmarks(int required)
{
    private readonly int _required = required;

    [Benchmark]
    public int MethodA() => _required;

    [Benchmark]
    public int MethodB() => _required;
}

/// <inheritdoc cref="UnconstructablePerClassBenchmarks" />
[BenchmarkCategory("unconstructable-permethod")]
[InstanceLifetime(InstanceLifetime.PerMethod)]
public class UnconstructablePerMethodBenchmarks(int required)
{
    private readonly int _required = required;

    [Benchmark]
    public int MethodA() => _required;

    [Benchmark]
    public int MethodB() => _required;
}
