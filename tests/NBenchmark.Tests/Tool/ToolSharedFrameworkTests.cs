using System.Diagnostics;
using System.Reflection;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Tool;

/// <summary>
///     The <c>dotnet benchmark</c> tool against a target that needs a shared framework it was not
///     started with.
/// </summary>
/// <remarks>
///     <para>
///         The tool has the worker's shared-framework problem about <i>itself</i>: unlike every other
///         mode, it loads the assembly under test into its own process, because discovery is
///         reflection over real types. So a <c>Microsoft.NET.Sdk.Web</c> target fails one boundary
///         earlier - <c>Assembly.LoadFrom</c> succeeds and the first <c>GetTypes()</c> throws
///         <c>Could not load file or assembly 'Microsoft.AspNetCore.Http.Abstractions'</c>.
///     </para>
///     <para>
///         Run as a process rather than by calling into the tool, because the fix is that the tool
///         restarts itself with a different framework set, and a process is the only place that is
///         observable.
///     </para>
/// </remarks>
public sealed class ToolSharedFrameworkTests
{
    /// <summary>
    ///     Suppresses the relaunch, so the control case can show the tool failing where it used to.
    ///     Duplicated from the tool's own private constant on purpose: a test that reads the value
    ///     under test cannot detect it changing, and this one should fail loudly if the marker is
    ///     renamed without the control being reconsidered.
    /// </summary>
    private const string RelaunchMarker = "NBENCHMARK_TOOL_RELAUNCHED";

    /// <summary>
    ///     <c>--list</c> is discovery and nothing else - the exact step that threw - so it proves the
    ///     fix without spending a measurement on it.
    /// </summary>
    [Fact]
    public void Tool_ListsBenchmarksInATargetThatNeedsASharedFramework()
    {
        var (exitCode, output) = RunTool(suppressRelaunch: false);

        Assert.Equal(0, exitCode);
        Assert.Contains("IsGet", output, StringComparison.Ordinal);
        Assert.Contains("HasPath", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The control. With the relaunch suppressed the tool cannot read the target, which is what
    ///     makes the test above evidence of the fix rather than evidence that ASP.NET Core is
    ///     installed on the machine running it.
    /// </summary>
    [Fact]
    public void Tool_WithoutTheRelaunch_CannotReadTheTarget()
    {
        var (_, output) = RunTool(suppressRelaunch: true);

        Assert.DoesNotContain("IsGet", output, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore", output, StringComparison.Ordinal);
    }

    /// <summary>
    ///     An ordinary console target is loaded directly. Asserted through the marker rather than by
    ///     timing or process counting: with the relaunch suppressed the result must be identical,
    ///     which is only true if no relaunch was going to happen.
    /// </summary>
    [Fact]
    public void Tool_DoesNotRelaunchForAnOrdinaryTarget()
    {
        var direct = RunTool(suppressRelaunch: true, target: IsolationFixtureLocator.AssemblyPath());
        var normal = RunTool(suppressRelaunch: false, target: IsolationFixtureLocator.AssemblyPath());

        Assert.Equal(0, normal.ExitCode);
        Assert.Equal(direct.ExitCode, normal.ExitCode);
        Assert.Contains("Fast", normal.Output, StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) RunTool(
        bool suppressRelaunch,
        string? target = null)
    {
        var startInfo = new ProcessStartInfo(WorkerLocator.ResolveDotnetMuxer())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(ToolAssemblyPath())!,
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(ToolAssemblyPath());
        startInfo.ArgumentList.Add("--assembly");
        startInfo.ArgumentList.Add(target ?? WebFixtureLocator.AssemblyPath());
        startInfo.ArgumentList.Add("--list");

        if (suppressRelaunch)
            startInfo.Environment[RelaunchMarker] = "1";

        using var process = Process.Start(startInfo)!;

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        Assert.True(process.WaitForExit(120_000), "The tool did not exit within two minutes.");

        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }

    /// <summary>
    ///     Located the same way as the other out-of-process fixtures, from an <c>AssemblyMetadata</c>
    ///     item baked in at build time.
    /// </summary>
    private static string ToolAssemblyPath()
    {
        var directory = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "ToolDirectory")
            ?.Value;

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException(
                "The ToolDirectory assembly metadata is missing. It is set by an AssemblyMetadata "
                + "item in NBenchmark.Tests.csproj.");
        }

        var path = Path.GetFullPath(Path.Combine(directory, "nbenchmark-tool.dll"));

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The benchmark tool was not found at '{path}'. It should have been built by the "
                + "ProjectReference in NBenchmark.Tests.csproj.");
        }

        return path;
    }
}
