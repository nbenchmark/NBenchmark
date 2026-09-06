using NBenchmark.Stats;
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
    ///     A strategy factory that captures cannot be addressed, so the whole group is refused rather
    ///     than being downgraded on the far side.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents is the quietest one available: the body is measured perfectly in a
    ///     worker and then scored with the built-in detector instead of the one the caller pinned, with
    ///     nothing in the output saying so. Only the inline-suite path used to check; the harness,
    ///     Single mode and the test integrations all discarded the refusal.
    /// </remarks>
    [Fact]
    public void ForDiscoveredClass_RefusesWhenAStrategyFactoryCaptures()
    {
        // A launcher that reports available, so worker deployment is not the variable under test -
        // this assertion is about what happens once a worker *is* known to be reachable.
        using var _ = FakeWorkerLauncher.Install(_ => throw new InvalidOperationException("not run"));

        var captured = new NeedsArguments(5);
        var options = MeasurementOptions.Default with { OutlierDetector = () => captured };

        var decision = WorkerRunPlan.ForDiscoveredClass(
            typeof(WorkerRunPlanTests).Assembly.Location, instanceSource: null, options);

        Assert.False(decision.CanIsolate);
        Assert.Equal(WorkerRunPlan.Refusal.UnrebuildableStrategy, decision.Refusal);

        // The reason travels with the numbers, not just with the console message.
        Assert.Equal(IsolationStatus.InProcessLiveFixture, decision.Status);
    }

    /// <summary>A static factory is addressable, so it is no obstacle at all.</summary>
    [Fact]
    public void ForDiscoveredClass_AllowsAStaticStrategyFactory()
    {
        using var _ = FakeWorkerLauncher.Install(_ => throw new InvalidOperationException("not run"));

        var options = MeasurementOptions.Default with { OutlierDetector = static () => new Rebuildable() };

        Assert.Null(WorkerRunPlan.UnrebuildableStrategy(options));

        Assert.True(WorkerRunPlan
            .ForDiscoveredClass(typeof(WorkerRunPlanTests).Assembly.Location, null, options)
            .CanIsolate);
    }

    /// <summary>
    ///     Both strategy slots are checked, not just the first. A significance test that cannot be
    ///     addressed is exactly as invisible as a detector that cannot.
    /// </summary>
    [Fact]
    public void UnrebuildableStrategy_ChecksTheSignificanceTestToo()
    {
        var captured = new NeedsArgumentsTest(3);
        var options = MeasurementOptions.Default with { SignificanceTest = () => captured };

        Assert.NotNull(WorkerRunPlan.UnrebuildableStrategy(options));
    }

    private sealed class NeedsArguments(int value) : IOutlierDetector
    {
        public string Name => $"needs-arguments ({value})";

        public OutlierClassification Classify(ReadOnlySpan<double> sortedSamples)
            => OutlierClassification.KeepAll(sortedSamples);
    }

    private sealed class Rebuildable : IOutlierDetector
    {
        public string Name => "rebuildable";

        public OutlierClassification Classify(ReadOnlySpan<double> sortedSamples)
            => OutlierClassification.KeepAll(sortedSamples);
    }

    private sealed class NeedsArgumentsTest(int value) : ISignificanceTest
    {
        public string Name => $"needs-arguments ({value})";

        public SignificanceReport Analyze(SignificanceContext context) => new() { Pairwise = [] };
    }
}
