using System.Reflection;
using NBenchmark.Workers;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Resolves <c>nbworker.dll</c> for the tests that spawn a real measurement worker. The path is
///     baked into this assembly at build time by an <c>AssemblyMetadata</c> item in
///     <c>NBenchmark.Tests.csproj</c>, so tests never guess at relative <c>bin</c> layouts.
/// </summary>
internal static class WorkerLocatorForTests
{
    public static string WorkerAssemblyPath()
    {
        var directory = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == WorkerLocator.WorkerDirectoryMetadataKey)
            ?.Value;

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                $"The {WorkerLocator.WorkerDirectoryMetadataKey} assembly metadata is missing. It is set "
                + "by an AssemblyMetadata item in NBenchmark.Tests.csproj.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, WorkerLocator.WorkerAssemblyFileName));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The measurement worker was not found at '{path}'. It should have been built by the "
                + "ProjectReference in NBenchmark.Tests.csproj.");
        }

        return path;
    }
}
