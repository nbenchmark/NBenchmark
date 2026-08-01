using System.Diagnostics;
using System.Reflection;
using NBenchmark.Workers;

namespace NBenchmark.Tool;

/// <summary>
///     Restarts the tool under the shared frameworks the assemblies it was asked to benchmark need.
/// </summary>
/// <remarks>
///     <para>
///         Unlike every other usage mode, the tool loads the target assembly into <i>its own</i>
///         process: discovery is reflection over real types, and the harness needs those types to
///         build the run plan before any worker exists. So a target from a
///         <c>Microsoft.NET.Sdk.Web</c> project fails the same way it failed in the worker, one
///         boundary earlier - <c>Assembly.LoadFrom</c> succeeds and the first
///         <c>GetTypes()</c> throws, because <c>Microsoft.AspNetCore.App</c> is on no list the tool's
///         process has.
///     </para>
///     <para>
///         The tool cannot fix this in place. A process's framework set is chosen by <c>hostfxr</c>
///         before managed code runs, so the only process that can load the target is one that has not
///         started yet - which, here, is the tool itself. It relaunches with the merged config that
///         <see cref="SharedFrameworkConfig" /> already builds for workers, hands the child the same
///         work, and forwards its exit code.
///     </para>
///     <para>
///         Discovery by <c>MetadataLoadContext</c> was the alternative and does not fit: it can answer
///         which methods carry <c>[Benchmark]</c>, but the harness goes on to instantiate the classes
///         and hand real delegates to the engine, so the executing types have to be real. A
///         reflection-only pass would have to be thrown away and redone the moment the run started.
///     </para>
/// </remarks>
internal static class FrameworkRelaunch
{
    /// <summary>
    ///     Set on the child so it cannot ask the same question and relaunch again. The child's own
    ///     framework set is not visible to <see cref="SharedFrameworkConfig" /> - it reads the tool's
    ///     <c>runtimeconfig.json</c> on disk, which is the same file either way - so without a marker
    ///     the answer would be identical and the relaunch would not terminate.
    /// </summary>
    private const string MarkerVariable = "NBENCHMARK_TOOL_RELAUNCHED";

    /// <summary>
    ///     Runs the tool's work in a relaunched process when the targets need it, and reports whether
    ///     it did.
    /// </summary>
    /// <param name="targetAssemblyPaths">
    ///     Every assembly this invocation will load. Their requirements are unioned, because they all
    ///     go into one process.
    /// </param>
    /// <param name="childArgs">
    ///     The command line for the child. Not the original <c>args</c>: a <c>--project</c> has
    ///     already been built by the time this is called, so the caller passes the built assembly
    ///     instead and the child does not repeat the build.
    /// </param>
    /// <param name="exitCode">The child's exit code, when one ran.</param>
    public static bool TryRelaunch(
        IReadOnlyList<string> targetAssemblyPaths,
        IReadOnlyList<string> childArgs,
        out int exitCode)
    {
        exitCode = 0;

        if (Environment.GetEnvironmentVariable(MarkerVariable) is not null)
            return false;

        var toolPath = Assembly.GetEntryAssembly()?.Location;

        // An empty location means a single-file or trimmed publish, where there is no dll for
        // `dotnet exec` to run. The tool is not shipped that way, but guessing at a path would turn
        // a working run into a confusing one.
        if (string.IsNullOrEmpty(toolPath))
            return false;

        if (SharedFrameworkConfig.ResolveFor(toolPath, targetAssemblyPaths) is not { } runtimeConfigPath)
            return false;

        var startInfo = new ProcessStartInfo(WorkerLocator.ResolveDotnetMuxer())
        {
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfigPath);
        startInfo.ArgumentList.Add(toolPath);

        foreach (var argument in childArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment[MarkerVariable] = "1";

        // Nothing is redirected. The child owns the console for the rest of the run - it writes the
        // results table, the progress display and any output from the benchmarked code, and piping
        // that through this process would cost the table its terminal width and its interactivity
        // for no benefit.
        using var child = Process.Start(startInfo);

        if (child is null)
        {
            Console.Error.WriteLine(
                "The benchmarked project needs a shared framework this tool was not started with, and "
                + "relaunching the tool to supply it failed to start a process.");

            exitCode = 1;

            return true;
        }

        child.WaitForExit();
        exitCode = child.ExitCode;

        return true;
    }
}
