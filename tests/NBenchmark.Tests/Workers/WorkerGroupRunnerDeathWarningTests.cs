using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     When a worker dies before its group finishes, the results it had already sent are not
///     trustworthy - the worker's own contract says nothing it sent can be assumed complete. The
///     warning that marks that is the only thing that lets a consumer's report distinguish a measured
///     row from a row measured by a process that then died, and it is stamped on the coordinator side
///     (where the death is observed) rather than in the worker.
/// </summary>
/// <remarks>
///     The helper is exercised directly here because the full path - a worker dying with a result
///     already on the wire - is an integration test against a real crash
///     (<see cref="HardCrashTests" />). The integration test proves the warning is stamped at the
///     right moment; these tests pin what "stamped" means for the row.
/// </remarks>
public sealed class WorkerGroupRunnerDeathWarningTests
{
    private static BenchmarkResult ResultNamed(string name, params string[] warnings) => new()
    {
        Name = name,
        Mean = 1,
        Median = 1,
        Percentiles = [],
        Min = 1,
        Max = 1,
        StandardDeviation = 0,
        Q1 = 1,
        Q3 = 1,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 1,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
        Warnings = warnings,
    };

    [Fact]
    public void NoResults_ReturnsEmpty()
    {
        Assert.Empty(WorkerGroupRunner.WithDeathWarning([]));
    }

    [Fact]
    public void OneResult_AppendsTheDeathWarning()
    {
        var stamped = WorkerGroupRunner.WithDeathWarning([ResultNamed("A")]);

        var row = Assert.Single(stamped);
        var warning = Assert.Single(row.Warnings);
        Assert.Contains("worker died", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingWarnings_ArePreservedAlongsideTheDeathWarning()
    {
        var stamped = WorkerGroupRunner.WithDeathWarning([ResultNamed("A", "existing caveat")]);

        var row = Assert.Single(stamped);
        Assert.Equal(2, row.Warnings.Count);
        Assert.Equal("existing caveat", row.Warnings[0]);
        Assert.Contains("worker died", row.Warnings[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EachResult_GetsTheWarning_IndependentOfTheOthers()
    {
        var stamped = WorkerGroupRunner.WithDeathWarning(
            [ResultNamed("A"), ResultNamed("B"), ResultNamed("C")]);

        Assert.Equal(3, stamped.Count);
        Assert.All(stamped, row => Assert.Single(row.Warnings));
        Assert.Equal(["A", "B", "C"], stamped.Select(r => r.Name));
    }
}