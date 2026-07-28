using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Unit coverage for the coordinator's per-replicate seed derivation.
/// </summary>
public sealed class WorkerRunPlanTests
{
    /// <summary>
    ///     A pinned session seed must make the whole run reproducible while still giving each
    ///     replicate a different order. Adding the replicate index to the seed would do the first but
    ///     not the second - adjacent seeds produce correlated shuffles - so the values are mixed.
    /// </summary>
    [Fact]
    public void DeriveSeed_IsDeterministicPerReplicate_AndDistinctAcrossThem()
    {
        var seeds = Enumerable.Range(0, 8).Select(r => WorkerRunPlan.DeriveSeed(12345, r)).ToList();

        Assert.All(seeds, s => Assert.NotNull(s));
        Assert.Equal(8, seeds.Distinct().Count());

        // Reproducible: the same session seed and replicate always give the same order.
        Assert.Equal(seeds, Enumerable.Range(0, 8).Select(r => WorkerRunPlan.DeriveSeed(12345, r)));

        // A different session seed gives a different set, so the seed actually controls the run.
        Assert.NotEqual(seeds, Enumerable.Range(0, 8).Select(r => WorkerRunPlan.DeriveSeed(999, r)));
    }

    /// <summary>
    ///     No pinned seed means each replicate is free to pick its own, so nothing travels.
    /// </summary>
    [Fact]
    public void DeriveSeed_WithNoSessionSeed_IsNull()
        => Assert.Null(WorkerRunPlan.DeriveSeed(null, 3));

    /// <summary>
    ///     A strategy object travels as a type name, which only works when the worker can construct
    ///     it. One that needs constructor arguments is reported rather than silently replaced by the
    ///     built-in strategy.
    /// </summary>
    [Fact]
    public void StrategyTypeName_RefusesAStrategyItCannotRebuild()
    {
        Assert.Null(WorkerRunPlan.StrategyTypeName(new NeedsArguments(5), out var refusal));
        Assert.Contains("parameterless constructor", refusal);

        var name = WorkerRunPlan.StrategyTypeName(new Rebuildable(), out var ok);
        Assert.Null(ok);
        Assert.Contains(nameof(Rebuildable), name);
    }

    private sealed class NeedsArguments(int value)
    {
        public int Value { get; } = value;
    }

    private sealed class Rebuildable;
}
