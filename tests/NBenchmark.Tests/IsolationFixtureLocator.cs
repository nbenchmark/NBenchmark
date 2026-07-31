using System.Reflection;

namespace NBenchmark.Tests;

/// <summary>
///     Resolves the <c>NBenchmark.Tests.IsolationFixture</c> executable that real-child-process
///     tests launch. The path is baked into this assembly at build time by an
///     <c>AssemblyMetadata</c> item in <c>NBenchmark.Tests.csproj</c>, so tests never guess at
///     relative <c>bin</c> layouts or depend on the working directory.
/// </summary>
internal static class IsolationFixtureLocator
{
    public const string AssemblyName = "NBenchmark.Tests.IsolationFixture";

    /// <summary>Fully-qualified name of a benchmark class inside the fixture.</summary>
    public static string ClassFullName(string className) => $"{AssemblyName}.{className}";

    /// <summary>Absolute path to the fixture's entry assembly, for <c>dotnet exec</c>.</summary>
    public static string AssemblyPath()
    {
        var directory = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "IsolationFixtureDirectory")
            ?.Value;

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The IsolationFixtureDirectory assembly metadata is missing. It is set by an "
                + "AssemblyMetadata item in NBenchmark.Tests.csproj.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, $"{AssemblyName}.dll"));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The isolation fixture was not found at '{path}'. It should have been built by the "
                + "ProjectReference in NBenchmark.Tests.csproj.");
        }

        return path;
    }
}
