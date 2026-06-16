namespace NBenchmark.Engine;

internal interface IProcessLauncher
{
    Task<IReadOnlyList<IsolatedResultItem>> LaunchAsync(
        IsolatedRunRequest request,
        CancellationToken cancellationToken);
}
