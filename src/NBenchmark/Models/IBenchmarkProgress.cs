namespace NBenchmark;

/// <summary>
///     Lifecycle callbacks for a running suite: which benchmark started, how far the warmup and the
///     measurement have got, and what each one produced.
/// </summary>
/// <remarks>
///     <para>
///         Every member has a no-op default, so an implementation overrides only the events it
///         reacts to - a progress bar that only needs "benchmark n of m" implements one method.
///     </para>
///     <para>
///         The engine awaits these, so a slow implementation slows the run down. It calls them
///         between samples rather than inside the timed region, so a callback cannot corrupt a
///         measurement - but it can still stretch a suite. For live per-sample telemetry with a
///         non-blocking contract, implement <see cref="IMeasurementObserver" /> instead.
///     </para>
/// </remarks>
public interface IBenchmarkProgress
{
    /// <summary>Signals that a suite is about to run <paramref name="total" /> benchmarks.</summary>
    public Task OnSuiteStartingAsync(
        IReadOnlyList<string> benchmarkNames, int total, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Signals that the warmup phase is starting.</summary>
    /// <param name="name">The benchmark being warmed up.</param>
    /// <param name="totalWarmupSamples">
    ///     The planned warmup count, or a value &lt;= 0 when warmup length is auto-resolved by the
    ///     plateau rule and not known in advance. Progress UIs should treat a non-positive total as
    ///     an indeterminate phase (no percentage or ETA).
    /// </param>
    /// <param name="cancellationToken">The run's cancellation token.</param>
    public Task OnWarmupStartingAsync(string name, int totalWarmupSamples, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Signals that the warmup phase for <paramref name="name" /> has finished.</summary>
    public Task OnWarmupCompletedAsync(string name, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Signals that benchmark <paramref name="index" /> of <paramref name="total" /> is starting.</summary>
    public Task OnBenchmarkStartingAsync(string name, int index, int total, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Signals that a measured sample completed.</summary>
    /// <param name="name">The benchmark being measured.</param>
    /// <param name="sample">The 1-based index of the sample that just completed.</param>
    /// <param name="totalSamples">
    ///     The planned sample total, or a value &lt;= 0 when the count is auto-resolved (the loop
    ///     stops on a confidence-interval target) and not known in advance. Progress UIs should
    ///     treat a non-positive total as indeterminate and avoid showing a percentage or ETA.
    /// </param>
    /// <param name="cancellationToken">The run's cancellation token.</param>
    public Task OnSampleCompletedAsync(
        string name, int sample, int totalSamples, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    ///     Signals that one benchmark finished, with its result - including a result that
    ///     <see cref="BenchmarkResult.Errored" />.
    /// </summary>
    public Task OnBenchmarkCompletedAsync(BenchmarkResult result, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>Signals that the suite finished, with every result it produced.</summary>
    public Task OnSuiteCompletedAsync(
        IReadOnlyList<BenchmarkResult> results, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>
///     The progress sink that reports nothing. It is the engine's default, and passing it
///     explicitly is how a caller silences a progress display it would otherwise inherit.
/// </summary>
/// <remarks>
///     Every member of <see cref="IBenchmarkProgress" /> defaults to a no-op, so this class
///     declares none: it exists as a singleton the engine can compare against, not as a set of
///     empty overrides. Mirrors <see cref="NullMeasurementObserver" />.
/// </remarks>
public sealed class NullBenchmarkProgress : IBenchmarkProgress
{
    /// <summary>The shared instance. The type is stateless, so there is never a reason for another.</summary>
    public static readonly NullBenchmarkProgress Instance = new();
}
