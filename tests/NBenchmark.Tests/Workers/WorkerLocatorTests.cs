using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Where the worker is looked for.
/// </summary>
/// <remarks>
///     <para>
///         Untested before, which is how the directory-scoped lookup came to check only a flat
///         <c>dir/nbworker.dll</c> while the build targets deploy to <c>dir/nbworker/</c>. In this
///         repository a flat copy also happens to exist in a multi-runtime build output, so that path
///         kept working by accident - but the subdirectory is the layout
///         <c>build/NBenchmark.targets</c> produces for a package consumer, and it was the only one
///         not being looked for.
///     </para>
///     <para>
///         The stakes are why this is worth pinning: the caller's response to "no worker" is to
///         measure in-process and carry on, so a lookup that misses reads as a quiet loss of
///         isolation rather than as an error.
///     </para>
/// </remarks>
public sealed class WorkerLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"nb-locator-{Guid.NewGuid():N}");

    public WorkerLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    ///     The shipped layout. The worker carries its own <c>runtimeconfig.json</c> and
    ///     <c>deps.json</c>, which describe a different program, so it cannot share a directory with
    ///     the application - the subdirectory is not incidental.
    /// </summary>
    [Fact]
    public void The_Deployed_Subdirectory_Layout_Is_Found()
    {
        var expected = PlaceWorker(Path.Combine(_root, "nbworker"));

        Assert.Equal(expected, WorkerLocator.ForOutputDirectory(_root));
    }

    [Fact]
    public void A_Flat_Layout_Is_Also_Found()
    {
        var expected = PlaceWorker(_root);

        Assert.Equal(expected, WorkerLocator.ForOutputDirectory(_root));
    }

    [Fact]
    public void The_Subdirectory_Wins_When_Both_Layouts_Are_Present()
    {
        PlaceWorker(_root);
        var subdirectory = PlaceWorker(Path.Combine(_root, "nbworker"));

        Assert.Equal(subdirectory, WorkerLocator.ForOutputDirectory(_root));
    }

    [Fact]
    public void A_Directory_Without_A_Worker_Resolves_To_Nothing()
    {
        Assert.Null(WorkerLocator.ForOutputDirectory(_root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_Absent_Directory_Resolves_To_Nothing(string? directory)
    {
        Assert.Null(WorkerLocator.ForOutputDirectory(directory));
    }

    /// <summary>
    ///     The lookup the tool needs: the assembly under test names its own directory, and the worker
    ///     built against it sits there.
    /// </summary>
    [Fact]
    public void A_Worker_Beside_The_Assembly_Under_Test_Is_Found()
    {
        var expected = PlaceWorker(Path.Combine(_root, "nbworker"));
        var target = Path.Combine(_root, "Target.dll");

        File.WriteAllText(target, "");

        Assert.Equal(expected, WorkerLocator.ForAssembly(target));
    }

    [Fact]
    public void An_Assembly_With_No_Worker_Beside_It_Resolves_To_Nothing()
    {
        var target = Path.Combine(_root, "Target.dll");

        File.WriteAllText(target, "");

        Assert.Null(WorkerLocator.ForAssembly(target));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void An_Absent_Assembly_Path_Resolves_To_Nothing(string? assemblyPath)
    {
        Assert.Null(WorkerLocator.ForAssembly(assemblyPath));
    }

    /// <summary>
    ///     A diagnostic that lists only the application's own directory sends the reader to fix the
    ///     wrong build, so the target's directory is named first.
    /// </summary>
    [Fact]
    public void The_Search_Diagnostic_Names_The_Target_Directory_First()
    {
        var target = Path.Combine(_root, "Target.dll");

        var description = WorkerLocator.DescribeSearch(target);

        Assert.Contains(_root, description);

        Assert.True(
            description.IndexOf(_root, StringComparison.Ordinal)
            < description.IndexOf(AppContext.BaseDirectory, StringComparison.Ordinal),
            $"The target directory should be named before the application's. Got: {description}");
    }

    [Fact]
    public void The_Search_Diagnostic_Still_Works_With_No_Target()
    {
        Assert.Contains(AppContext.BaseDirectory, WorkerLocator.DescribeSearch());
    }

    /// <summary>
    ///     A worker beside the code under test makes isolation possible even when the running
    ///     application has none - which is exactly the <c>dotnet benchmark --assembly</c> case.
    /// </summary>
    [Fact]
    public void Availability_Considers_The_Assembly_Under_Test()
    {
        PlaceWorker(Path.Combine(_root, "nbworker"));
        var target = Path.Combine(_root, "Target.dll");

        File.WriteAllText(target, "");

        Assert.True(WorkerLauncher.Current.IsAvailableFor(target));
    }

    private static string PlaceWorker(string directory)
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, WorkerLocator.WorkerAssemblyFileName);
        File.WriteAllText(path, "");

        return Path.GetFullPath(path);
    }
}
