using System.Reflection;

namespace NBenchmark.Tests;

/// <summary>
///     Resolves the <c>NBenchmark.Tests.WebFixture</c> assembly - a benchmark target that needs the
///     ASP.NET Core shared framework. Located the same way as
///     <see cref="IsolationFixtureLocator" />, from an <c>AssemblyMetadata</c> item baked in at build
///     time, so tests never guess at relative <c>bin</c> layouts.
/// </summary>
internal static class WebFixtureLocator
{
    public const string AssemblyName = "NBenchmark.Tests.WebFixture";

    /// <summary>Fully-qualified name of a benchmark class inside the fixture.</summary>
    public static string ClassFullName(string className) => $"{AssemblyName}.{className}";

    /// <summary>Absolute path to the fixture assembly, for a worker to load by path.</summary>
    public static string AssemblyPath()
    {
        var directory = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "WebFixtureDirectory")
            ?.Value;

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The WebFixtureDirectory assembly metadata is missing. It is set by an "
                + "AssemblyMetadata item in NBenchmark.Tests.csproj.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, $"{AssemblyName}.dll"));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The web fixture was not found at '{path}'. It should have been built by the "
                + "ProjectReference in NBenchmark.Tests.csproj.");
        }

        return path;
    }
}
