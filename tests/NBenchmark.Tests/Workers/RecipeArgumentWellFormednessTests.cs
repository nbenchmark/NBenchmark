using NBenchmark.Engine;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     A10: a recipe's own argument slot is checked for well-formedness on the worker's side, the
///     same way a body's slot already was.
/// </summary>
/// <remarks>
///     <see cref="ArgumentSource.IsWellFormed" /> exists because the coordinator's own construction
///     sets exactly one of <see cref="ArgumentSource.Value" /> and <see cref="ArgumentSource.Recipe" />,
///     but the worker is reading a frame off a pipe rather than trusting that invariant - a
///     hand-rolled payload, or a future bug that builds one wrong, can carry both or neither.
///     The worker's own <c>BodyResolver</c> already called it for a body's slots; a recipe's
///     <i>own</i> arguments - <c>prepare: (int size) =&gt; Build(size)</c> paired with a value for
///     <c>size</c> - went straight to a null check on <see cref="ArgumentSource.Value" /> instead,
///     which silently prefers the value and never says the recipe was there too.
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class RecipeArgumentWellFormednessTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public RecipeArgumentWellFormednessTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SingleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static int Zero() => 0;

    /// <summary>
    ///     Corrupts a well-formed recipe argument the way a malformed frame would - both an encoded
    ///     value and a nested factory for the same parameter - and asserts the group is refused by
    ///     name rather than silently measuring the byte array the value would have built.
    /// </summary>
    [Fact]
    public async Task A_Recipe_Argument_Carrying_Both_A_Value_And_A_Factory_Is_Refused()
    {
        static byte[] Prepare(int size) => new byte[size];
        static void Body(byte[] data) => throw new InvalidOperationException($"len={data.Length}");

        var receivers = new ReceiverTable(MeasurementOptions.Default.MaxTransferredStateBytes);

        Assert.True(BodyRef.TryCreate(
            Body,
            "malformed-recipe-arg",
            out var bodyRef,
            out _,
            arguments: null,
            recipes: [StateRecipe.For(Prepare, 4096)],
            receivers: receivers));

        var recipe = bodyRef.Arguments[0].Recipe!;

        Assert.True(AddressedFactory.TryCreate(Zero, "a nested factory", out var nestedFactory, out _));

        // The exact shape IsWellFormed exists to catch: the parameter's slot now makes two different
        // claims about where its value comes from.
        var malformedSlot = recipe.Body!.Arguments[0] with { Recipe = nestedFactory };
        var malformedRecipe = recipe with { Body = recipe.Body with { Arguments = [malformedSlot] } };
        var malformedBody = bodyRef with { Arguments = [bodyRef.Arguments[0] with { Recipe = malformedRecipe }] };

        var request = new RunGroupPayload
        {
            GroupId = "malformed-recipe-arg",
            Kind = WorkGroupKind.Lambdas,
            TargetAssemblyPath = malformedBody.AssemblyPath,
            Bodies = [malformedBody],
            Receivers = receivers.Receivers,
            Options = MeasurementOptions.Default with { Iterations = 2, WarmupIterations = 0, OpsPerSample = 1 },
            TotalBenchmarks = 1,
        };

        var group = await WorkerLauncher.Current.RunGroupAsync(
            request,
            NullBenchmarkProgress.Instance,
            NullMeasurementObserver.Instance,
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        // Refused, naming the conflict - not a clean result computed from the silently-preferred value.
        Assert.Empty(group.Results);
        Assert.Contains(group.Faults, f => f.Message.Contains("carries both"));
    }
}
