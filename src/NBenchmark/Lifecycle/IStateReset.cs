namespace NBenchmark.Lifecycle;

/// <summary>
///     Implemented by a benchmark class that uses <c>[InstanceLifetime(InstanceLifetime.PerClass)]</c>
///     to declare how its shared instance state is reset between <c>[Benchmark]</c> methods. When the
///     harness detects that the benchmark class implements this interface, it invokes
///     <see cref="ResetAsync" /> between benchmark methods (after one method completes and before the
///     next method's warmup phase) so each method observes a clean slate and the
///     statistical-independence assumption of the significance test is preserved.
/// </summary>
/// <remarks>
///     <para>
///         The class owns its reset semantics and fans the reset out to whatever it holds - a
///         <c>DbContext</c> clears its change tracker, a cache drops its entries, a counter resets to
///         zero, etc.
///     </para>
///     <para>
///         This means one thing only: <i>I reset between methods, so PerClass is safe</i>. An empty
///         body is not a way to say the sharing is deliberate - say that with
///         <c>[SharedState]</c> instead, which claims nothing about resetting and so cannot be
///         contradicted by its own body. The two used to be the same declaration, and because the
///         engine can only see that the interface is present, <c>return Task.CompletedTask;</c>
///         silenced every safeguard while changing nothing at all; analyzer NB0011 now reports that
///         shape.
///     </para>
///     <para>
///         Between <i>launches</i> nothing is asked of it: the instance is rebuilt and
///         <c>[BenchmarkSetup]</c> runs again, which is strictly more than a reset. The callback
///         covers the gaps between methods within one launch, and only those.
///     </para>
/// </remarks>
public interface IStateReset
{
    /// <summary>
    ///     Resets the shared instance state. Called by the engine between <c>[Benchmark]</c>
    ///     methods when <see cref="InstanceLifetime.PerClass" /> is in effect, after the previous
    ///     method's teardown and before the next method's warmup. The cancellation token is the
    ///     run-level token; implementations should forward it to any async work they await.
    /// </summary>
    public Task ResetAsync(CancellationToken cancellationToken);
}
