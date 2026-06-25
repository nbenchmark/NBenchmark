namespace NBenchmark.Lifecycle;

/// <summary>
///     Implemented by a benchmark class that uses <c>[InstanceLifetime(InstanceLifetime.PerClass)]</c>
///     to declare how its shared instance state is reset between <c>[Benchmark]</c> methods. When the
///     host detects that the benchmark class implements this interface, it invokes
///     <see cref="ResetAsync" /> between benchmark methods (after one method completes and before the
///     next method's warmup phase) so each method observes a clean slate and the
///     statistical-independence assumption of the significance test is preserved.
/// </summary>
/// <remarks>
///     The class owns its reset semantics and fans the reset out to whatever it holds - a
///     <c>DbContext</c> clears its change tracker, a cache drops its entries, a counter resets to
///     zero, etc. A no-op implementation (<c>return Task.CompletedTask;</c>) is valid and declares
///     that the shared state is intentionally carried across methods; this also opts the class out
///     of the auto-isolation fallback.
/// </remarks>
public interface IStateReset
{
    /// <summary>
    ///     Resets the shared instance state. Called by the engine between <c>[Benchmark]</c>
    ///     methods when <see cref="InstanceLifetime.PerClass" /> is in effect, after the previous
    ///     method's teardown and before the next method's warmup. The cancellation token is the
    ///     run-level token; implementations should forward it to any async work they await.
    /// </summary>
    Task ResetAsync(CancellationToken cancellationToken);
}