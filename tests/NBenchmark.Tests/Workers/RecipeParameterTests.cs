using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     A parameter sweep whose values are built in the measuring process rather than sent to it.
/// </summary>
/// <remarks>
///     <para>
///         The value form of <c>WithParameter</c> can only carry what <see cref="TestArgumentCodec" />
///         carries, so a sweep over two payloads, two documents or two pre-built trees was refused for
///         its type - which is the shape a parameter sweep is <i>for</i>. The recipe form sends the
///         instructions instead, exactly as prepared state does.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class RecipeParameterTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public RecipeParameterTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SingleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static BenchmarkSuite Fast(BenchmarkSuite suite) => suite
        .WithIterations(16)
        .WithWarmup(1)
        .WithOpsPerSample(1)
        .WithAutoTune(AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(5),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        });

    /// <summary>
    ///     A recipe-valued sweep is isolated, and each arm gets the value its own recipe built.
    /// </summary>
    /// <remarks>
    ///     The body throws unless the array it received matches the label the row is named after, so a
    ///     run that invoked the wrong recipe - or the same one twice - errors rather than reporting two
    ///     plausible numbers under two names.
    /// </remarks>
    [Fact]
    public async Task RecipeValuedParameter_IsIsolated_AndEachArmGetsItsOwnValue()
    {
        var results = await Fast(new BenchmarkSuite("payloads")
                .WithParameter("payload", ("small", static () => new byte[64]), ("large", static () => new byte[4096]))
                .Add("consume", static (byte[] payload) => payload.Length is 64 or 4096
                    ? payload.Length
                    : throw new InvalidOperationException($"payload arrived with {payload.Length} bytes.")))
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));

        // The label names the row, because a recipe has no value to ask until it runs - in the other
        // process.
        Assert.Contains(results, r => r.Name.Contains("payload=small", StringComparison.Ordinal));
        Assert.Contains(results, r => r.Name.Contains("payload=large", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A recipe sweep beside a value sweep is isolated, with each parameter filled its own way.
    /// </summary>
    /// <remarks>
    ///     This is the combination the old wire could not express at all: the address carried either a
    ///     list of encoded values or one prepared slot, so a body wanting one of each was refused. Per
    ///     slot, the two are the same thing.
    /// </remarks>
    [Fact]
    public async Task RecipeAndValueParameters_Mixed_AreBothIsolated()
    {
        var results = await Fast(new BenchmarkSuite("mixed")
                .WithParameter("payload", ("small", static () => new byte[32]))
                .WithParameter("repeats", 1, 2)
                .Add("consume", static (byte[] payload, int repeats) => payload.Length == 32 && repeats is 1 or 2
                    ? payload.Length * repeats
                    : throw new InvalidOperationException($"got {payload.Length} bytes and {repeats} repeats.")))
            .RunAsync();

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));
    }

    /// <summary>
    ///     On an isolated run the coordinator never invokes the recipe: the worker builds its own.
    /// </summary>
    /// <remarks>
    ///     Not an optimisation - a recipe is user code, and one that opens a file or a connection would
    ///     do so in a process with no benchmark in it. The eager version of this mistake is the one the
    ///     host-side service-provider resolvers were fixed for.
    /// </remarks>
    [Fact]
    public async Task RecipeValuedParameter_IsNotBuiltInTheCoordinator_WhenIsolated()
    {
        RecipeProbe.Builds = 0;

        var results = await Fast(new BenchmarkSuite("deferred")
                .WithParameter("payload", ("probe", RecipeProbe.Build))
                .Add("consume", static (byte[] payload) => payload.Length))
            .RunAsync();

        Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        Assert.All(results, r => Assert.Equal(IsolationStatus.Isolated, r.IsolationStatus));

        Assert.Equal(0, RecipeProbe.Builds);
    }

    /// <summary>A recipe with no label is refused, because nothing else can name the row.</summary>
    [Fact]
    public void RecipeValuedParameter_WithoutALabel_IsRefused()
    {
        var suite = new BenchmarkSuite("unlabelled");

        var error = Assert.Throws<ArgumentException>(
            () => suite.WithParameter("payload", ("", static () => new byte[1])));

        Assert.Contains("label", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A counter in the coordinator's own process, so "was this built here" is answerable. A static
    ///     method rather than a lambda, so the recipe is addressable.
    /// </summary>
    private static class RecipeProbe
    {
        public static int Builds;

        public static byte[] Build()
        {
            Builds++;

            return new byte[16];
        }
    }
}
