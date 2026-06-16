using NBenchmark.Engine;

namespace NBenchmark.Tests;

internal sealed class FakeProcessLauncher : IProcessLauncher
{
    private readonly Func<IsolatedRunRequest, CancellationToken, Task<IReadOnlyList<IsolatedResultItem>>> _handler;

    public FakeProcessLauncher(
        Func<IsolatedRunRequest, CancellationToken, Task<IReadOnlyList<IsolatedResultItem>>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public Task<IReadOnlyList<IsolatedResultItem>> LaunchAsync(
        IsolatedRunRequest request,
        CancellationToken cancellationToken)
        => _handler(request, cancellationToken);
}
