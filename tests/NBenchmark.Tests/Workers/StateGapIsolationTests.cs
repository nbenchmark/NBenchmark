using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     The shapes that used to cost a run its isolation for reasons unrelated to what was being
///     measured, each now measured in a real worker.
/// </summary>
/// <remarks>
///     <para>
///         Every case here was refused by the address rather than by anything about the benchmark: a
///         prepare delegate that needed one number, a body over two inputs, a sweep whose values were
///         a shape too complex to encode, a generic method. They are grouped because they are one
///         mechanism - a parameter slot filled either by a value or by a recipe - and because the
///         earlier design had a separate refusal for each.
///     </para>
///     <para>
///         Measured end to end rather than asserted on the address, wherever the state has to survive
///         a process boundary intact. An address that looks right and rehydrates to the wrong value is
///         precisely the failure mode this whole area exists to prevent, and only running it in a
///         worker can tell the two apart.
///     </para>
/// </remarks>
[Collection(nameof(RealWorkerCollection))]
public sealed class StateGapIsolationTests : IDisposable
{
    private readonly IWorkerLauncher _prior = WorkerLauncher.Current;

    public StateGapIsolationTests()
    {
        WorkerLauncher.Current = new RealWorkerLauncher(WorkerLocatorForTests.WorkerAssemblyPath());
        SimpleModeGuidance.ResetForTesting();
    }

    public void Dispose() => WorkerLauncher.Current = _prior;

    private static void AssertIsolated(BenchmarkResult result)
        => Assert.True(
            result.IsolationStatus == IsolationStatus.Isolated,
            $"expected an isolated measurement, got {result.IsolationStatus}. "
            + $"Warnings: {string.Join(" | ", result.Warnings)}");

    private static MeasurementOptions FastOptions => MeasurementOptions.Default with
    {
        Iterations = 16,
        WarmupIterations = 1,
        OpsPerSample = 1,
        AutoTune = AutoTuneOptions.Default with
        {
            MaxTuningTime = TimeSpan.FromSeconds(5),
            MinWarmupTime = TimeSpan.Zero,
            MinMeasurementTime = TimeSpan.Zero,
            RequireJitQuiescence = false,
            EnableJitterCalibration = false,
        },
    };

    // ---------- W-07: arguments on the prepare delegate ----------

    /// <summary>
    ///     A prepare delegate that takes the value it would otherwise have captured is isolated, and the
    ///     value arrives intact.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The refusal this closes is the one users reach <i>after</i> doing the rewrite the
    ///         diagnostic asked for: splitting <c>var d = Build(size); Run(() =&gt; Sort(d))</c> moves the
    ///         capture from the body into the prepare delegate, which is refused for exactly the same
    ///         reason.
    ///     </para>
    ///     <para>
    ///         The <i>value</i> is asserted from inside the body, not from a return value. The
    ///         measurement happens in another process, so nothing this process can read afterwards says
    ///         what the worker actually saw - but a body that throws comes back as an errored result with
    ///         its message, and a recipe invoked with a defaulted argument would have built an empty
    ///         array. Status alone would pass either way.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Run_WithPrepareArgument_IsIsolated_AndBuildsTheRequestedSize()
    {
        var result = Benchmark.Run(
            prepare: static (int size) => new byte[size],
            prepareArgument: 4096,
            body: static data => data.Length == 4096
                ? data.Length
                : throw new InvalidOperationException($"the recipe built {data.Length} bytes, not 4096."),
            options: FastOptions,
            name: "sized");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);
    }

    // ---------- W-08: more than one prepared value ----------

    /// <summary>
    ///     A body over two independently prepared values is isolated, and each slot gets its own recipe.
    /// </summary>
    /// <remarks>
    ///     The address used to carry one prepared slot, so this shape was written by hand-tupling the
    ///     pair into a single state and destructuring it in the body - boilerplate that existed because
    ///     of the wire rather than because a benchmark over two inputs is unusual.
    /// </remarks>
    [Fact]
    public void Run_WithTwoPreparedValues_IsIsolated_AndBothArriveIntact()
    {
        var result = Benchmark.Run(
            prepare1: static () => new byte[64],
            prepare2: static () => new byte[8],
            body: static (haystack, needle) => haystack.Length == 64 && needle.Length == 8
                ? haystack.Length + needle.Length
                : throw new InvalidOperationException(
                    $"slots arrived as {haystack.Length} and {needle.Length}, not 64 and 8."),
            options: FastOptions,
            name: "two-slots");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);
    }

    // ---------- W-10: per-iteration setup over prepared state ----------

    /// <summary>
    ///     The canonical sort benchmark, which could not be written correctly in Single mode at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>prepare</c> runs once, so <c>d =&gt; Array.Sort(d)</c> sorts an already-sorted array
    ///         from the second sample onward and reports the cost of doing nothing. The setup hook is
    ///         what makes each sample measure a sort; it runs outside the timed region and receives the
    ///         same array the body does.
    ///     </para>
    ///     <para>
    ///         Asserted by having the body <i>observe</i> that the setup reached the array it reads -
    ///         a hook bound to a private copy would leave the body's array sorted, and no timing
    ///         assertion could tell that apart from a fast machine.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Run_WithPreparedStateAndIterationSetup_ResetsTheArrayTheBodyReads()
    {
        var result = Benchmark.Run(
            prepare: static () => new int[64],
            body: static data =>
            {
                // The setup writes a descending sequence into this array. Reading it before sorting is
                // what proves the hook and the body share one object: bound to a copy, the first
                // element would still be 0 from the second sample onward.
                if (data[0] != data.Length)
                    throw new InvalidOperationException($"setup did not reach the body's array (saw {data[0]}).");

                Array.Sort(data);
            },
            setup: static data =>
            {
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] = data.Length - i;
                }
            },
            options: FastOptions,
            name: "sort");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);
    }

    /// <summary>
    ///     A per-iteration hook that captures costs the benchmark its isolation rather than being
    ///     dropped.
    /// </summary>
    /// <remarks>
    ///     A body measured with its setup silently missing produces a plausible number for work that
    ///     never happened, which is worse than a labelled fallback. Refused with the hook named, so the
    ///     reader knows which of the three delegates to fix.
    /// </remarks>
    [Fact]
    public async Task Run_WithCapturingIterationSetup_FallsBackAndNamesTheHook()
    {
        using var stderr = new StringWriter();
        var priorError = Console.Error;
        Console.SetError(stderr);

        var marker = new MemoryStream(new byte[8]);
        BenchmarkResult result;

        try
        {
            result = Benchmark.Run(
                prepare: static () => new int[8],
                body: static data => data.Length,
                setup: data => marker.Position = data.Length % 4,

                // The labelled fallback, not the hard error: this test is about the hook being named
                // in the guidance, which only the fallback path prints.
                options: FastOptions with { RequireIsolation = false },
                name: "capturing-setup");
        }
        finally
        {
            Console.SetError(priorError);
        }

        await Task.CompletedTask;

        Assert.False(result.Errored, result.ErrorMessage);
        Assert.True(result.IsolationStatus.IsRefusal(), $"expected a refusal, got {result.IsolationStatus}");
        Assert.Contains("setup", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------- W-09: generic contexts ----------

    /// <summary>
    ///     A closed generic method is isolated rather than refused for being generic.
    /// </summary>
    /// <remarks>
    ///     A metadata token names the <i>open</i> definition, so <c>Length&lt;int&gt;</c> and
    ///     <c>Length&lt;string&gt;</c> share one address and neither can be invoked from it. Carrying the
    ///     type arguments alongside is the rest of the address; without them the worker resolved
    ///     something it could not call, which is why the whole shape was declined.
    /// </remarks>
    [Fact]
    public void Run_OverAClosedGenericMethod_IsIsolated()
    {
        var result = Benchmark.Run(
            static () => GenericProbe.Count<string>(),
            options: FastOptions,
            name: "generic-method");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);
    }

    /// <summary>
    ///     A body declared inside a generic method - where Roslyn puts the closure class on a generic
    ///     type - is isolated over the closed type argument.
    /// </summary>
    [Fact]
    public void Run_InsideAGenericMethod_IsIsolated() => Assert.True(MeasureGenerically<long>());

    private bool MeasureGenerically<T>()
    {
        var result = Benchmark.Run(
            static () => typeof(T) == typeof(long)
                ? typeof(T).Name.Length
                : throw new InvalidOperationException($"the closure was closed over {typeof(T).Name}, not Int64."),
            options: FastOptions,
            name: "generic-context");

        Assert.False(result.Errored, result.ErrorMessage);
        AssertIsolated(result);

        return true;
    }

    private static class GenericProbe
    {
        /// <summary>
        ///     Throws unless the worker closed the method over the type argument the address carried.
        ///     Resolving the open definition and closing it over the wrong thing - or over
        ///     <see cref="object" /> as a stand-in - would otherwise be indistinguishable from success.
        /// </summary>
        public static int Count<T>() => typeof(T) == typeof(string)
            ? typeof(T).Name.Length
            : throw new InvalidOperationException($"the method was closed over {typeof(T).Name}, not String.");
    }

    // ---------- W-13: the transport guard does not fire spuriously ----------

    /// <summary>
    ///     Reflection-based serialization is enabled in an ordinary test host, so the transport guard
    ///     stays silent.
    /// </summary>
    /// <remarks>
    ///     The guard's whole value is that it fires <i>only</i> for a coordinator published with
    ///     reflection off. One that fired here would take every run in this repository in-process and
    ///     label it "no worker", which is a far worse failure than the one it prevents - and it would
    ///     look like a configuration problem rather than a bug.
    /// </remarks>
    [Fact]
    public void TransportRefusal_IsAbsent_InAnOrdinaryHost() => Assert.Null(FrameChannel.TransportRefusal);
}
