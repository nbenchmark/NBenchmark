using NBenchmark.Engine;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Run-order randomization has to survive the process boundary.
/// </summary>
/// <remarks>
///     <para>
///         Randomizing execution order is what turns "the second benchmark ran on a warmer cache" from
///         a fixed confound into a nuisance factor that averages out across replicates. It only works
///         if the process doing the measuring actually applies it, and the previous isolated path did
///         not: it hardcoded declaration order, so <see cref="RunOrder.Random" /> was silently
///         discarded whenever isolation was on.
///     </para>
///     <para>
///         The pivot made isolation the default, which turned that from an edge case into the normal
///         path - and reintroduced it for inline suites, whose request carried no order at all. These
///         tests pin the order on the request itself, because that is the one place a reader can check
///         it without spawning a process.
///     </para>
/// </remarks>
public sealed class IsolatedRunOrderTests
{
    private static WorkerGroupRunner.GroupResult Empty(RunGroupPayload request) => new()
    {
        Results = [],
        RawSamples = [],
        Faults = [new FaultPayload { Message = "not measuring in this test" }],
    };

    private static BenchmarkSuite Fast(BenchmarkSuite suite) => suite
        .WithIterations(2)
        .WithWarmup(0)
        .WithOpsPerSample(1);

    /// <summary>
    ///     An inline suite's configured order reaches the worker. The suite default is
    ///     <see cref="RunOrder.Random" />, so a request carrying
    ///     <see cref="RunOrder.Declaration" /> means the setting was dropped on the way.
    /// </summary>
    [Fact]
    public async Task InlineSuite_SendsItsRunOrderToTheWorker()
    {
        using var scope = FakeWorkerLauncher.Install(Empty);

        await Fast(new BenchmarkSuite("order")
                .WithSeed(1234)
                .Add("a", static () => 1)
                .Add("b", static () => 2))
            .RunAsync();

        var request = Assert.Single(scope.Launcher.Requests);

        Assert.Equal(RunOrder.Random, request.Order);
        Assert.NotNull(request.Seed);
    }

    /// <summary>
    ///     And an explicit <see cref="RunOrder.Declaration" /> reaches it too, rather than the worker
    ///     shuffling regardless. A parameter sweep and a deliberately ordered comparison both depend on
    ///     this being honoured in the direction the caller chose.
    /// </summary>
    [Fact]
    public async Task InlineSuite_SendsDeclarationOrderWhenAsked()
    {
        using var scope = FakeWorkerLauncher.Install(Empty);

        await Fast(new BenchmarkSuite("order")
                .WithRunOrder(RunOrder.Declaration)
                .Add("a", static () => 1)
                .Add("b", static () => 2))
            .RunAsync();

        Assert.Equal(RunOrder.Declaration, Assert.Single(scope.Launcher.Requests).Order);
    }

    /// <summary>
    ///     The worker's shuffle is reproducible from the seed it was sent, and the seed differs per
    ///     replicate - so each replicate randomizes order differently while the whole run replays
    ///     identically from one number.
    /// </summary>
    [Fact]
    public void ReplicateSeeds_ShuffleDifferentlyButReproducibly()
    {
        var bodies = new[] { "a", "b", "c", "d", "e", "f" };

        var first = RunOrdering.Apply(bodies, RunOrder.Random, WorkerRunPlan.DeriveSeed(99, 0));
        var second = RunOrdering.Apply(bodies, RunOrder.Random, WorkerRunPlan.DeriveSeed(99, 1));
        var firstAgain = RunOrdering.Apply(bodies, RunOrder.Random, WorkerRunPlan.DeriveSeed(99, 0));

        Assert.Equal(first, firstAgain);
        Assert.NotEqual(first, second);

        // A shuffle is a permutation: no body may be dropped or duplicated on the way.
        Assert.Equal(bodies.OrderBy(b => b, StringComparer.Ordinal), first.OrderBy(b => b, StringComparer.Ordinal));
    }

    /// <summary>
    ///     Declaration order is left exactly alone, including for a single-element list where a shuffle
    ///     would be indistinguishable from one.
    /// </summary>
    [Fact]
    public void DeclarationOrder_IsNotReordered()
    {
        var bodies = new[] { "a", "b", "c" };

        Assert.Equal(bodies, RunOrdering.Apply(bodies, RunOrder.Declaration, seed: 7));
        Assert.Equal(bodies, RunOrdering.Apply(bodies, RunOrder.Declaration, seed: null));
    }

    /// <summary>
    ///     Group-wise ordering keeps the groups in first-appearance order and shuffles only within
    ///     them. That is what a parameter sweep needs: the reader expects parameter values in the order
    ///     they were declared, and every comparison the table invites is within one value anyway.
    /// </summary>
    [Fact]
    public void GroupWiseOrdering_KeepsGroupsInPlace()
    {
        var items = new[] { "p1:a", "p1:b", "p1:c", "p2:a", "p2:b", "p2:c" };

        var ordered = RunOrdering.ApplyWithinGroups(
            items, RunOrder.Random, seed: 5, i => i.Split(':')[0]);

        Assert.All(ordered.Take(3), i => Assert.StartsWith("p1:", i, StringComparison.Ordinal));
        Assert.All(ordered.Skip(3), i => Assert.StartsWith("p2:", i, StringComparison.Ordinal));

        Assert.Equal(
            items.OrderBy(i => i, StringComparer.Ordinal),
            ordered.OrderBy(i => i, StringComparer.Ordinal));
    }
}
