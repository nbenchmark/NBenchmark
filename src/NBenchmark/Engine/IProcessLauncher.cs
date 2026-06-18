namespace NBenchmark.Engine;

internal interface IProcessLauncher
{
    public Task<IReadOnlyList<IsolatedResultItem>> LaunchAsync(
        IsolatedRunRequest request,
        CancellationToken cancellationToken);
}
