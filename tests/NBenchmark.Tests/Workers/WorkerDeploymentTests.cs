using System.Text.Json;
using NBenchmark.Workers;
using Xunit;

namespace NBenchmark.Tests.Workers;

/// <summary>
///     Asserts that a worker <i>directory</i> is self-sufficient - that the set of files shipped
///     beside <c>nbworker.dll</c> is enough for the .NET host to start it.
/// </summary>
/// <remarks>
///     <para>
///         This covers the one part of the design that no other test could reach. Every other worker
///         test launches the worker out of its own build output, which contains everything it needs
///         by construction. What ships to a package consumer is a <i>curated subset</i> of that
///         directory, selected by a target in <c>NBenchmark.csproj</c>, and a subset that is missing
///         one file fails before any NBenchmark code runs - the host resolver rejects it, the process
///         dies with no handshake, and the coordinator falls back to in-process measurement.
///     </para>
///     <para>
///         That is the worst available failure shape: the run still produces numbers, they are simply
///         the host's, and the only clue is a diagnostic about a worker that "exited before answering
///         the handshake". It shipped exactly once - <c>tools/&lt;tfm&gt;/</c> held nbworker's three
///         own files and not the <c>NBenchmark.dll</c> its <c>deps.json</c> requires - which is why
///         the assertion here is on the deps manifest rather than on a hardcoded file list. A future
///         dependency is covered without anyone remembering to add it.
///     </para>
/// </remarks>
public sealed class WorkerDeploymentTests
{
    /// <summary>
    ///     Every runtime assembly <c>nbworker.deps.json</c> declares must sit beside it. The host
    ///     resolves them relative to the application directory, which for <c>dotnet exec</c> is the
    ///     directory holding the dll - not the consuming application's output, one level up.
    /// </summary>
    [Fact]
    public void WorkerDirectory_ContainsEveryAssemblyItsDepsFileDeclares()
    {
        var workerPath = WorkerLocatorForTests.WorkerAssemblyPath();
        var directory = Path.GetDirectoryName(workerPath)!;

        var missing = DeclaredRuntimeAssemblies(workerPath)
            .Where(name => !File.Exists(Path.Combine(directory, name)))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"nbworker.deps.json declares assemblies that are not deployed beside it in '{directory}': "
            + $"{string.Join(", ", missing)}. The host resolver rejects the process before Main runs, so "
            + "the coordinator sees a worker that died without a handshake and silently measures "
            + "in-process instead. Add them to _NBenchmarkPackWorker in NBenchmark.csproj.");
    }

    /// <summary>
    ///     The deployable set, copied to a directory of its own, actually starts and completes a
    ///     handshake.
    /// </summary>
    /// <remarks>
    ///     The check above states the invariant; this one proves the conclusion drawn from it. They are
    ///     not redundant: a manifest can be satisfied and the process still fail to start (a bad
    ///     <c>runtimeconfig.json</c>, a framework roll-forward that does not apply), and that failure
    ///     is equally invisible from the coordinator's side.
    /// </remarks>
    [Fact]
    public async Task DeployableWorkerSet_StartsAndAnswersTheHandshake()
    {
        var workerPath = WorkerLocatorForTests.WorkerAssemblyPath();
        var source = Path.GetDirectoryName(workerPath)!;

        var staging = Path.Combine(
            Path.GetTempPath(),
            $"nbworker-deploy-{Guid.NewGuid():N}");

        Directory.CreateDirectory(staging);

        try
        {
            // The same selection the pack target makes: managed assemblies and the two json manifests
            // the host reads. Symbols are deliberately excluded, here and there.
            foreach (var file in Directory.EnumerateFiles(source)
                         .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                                     || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                File.Copy(file, Path.Combine(staging, Path.GetFileName(file)));
            }

            await using var worker = await WorkerHost.StartAsync(
                Path.Combine(staging, WorkerLocator.WorkerAssemblyFileName),
                RuntimeProfile.SteadyState,
                CancellationToken.None);

            Assert.Equal(WorkerProtocol.Version, worker.Ready.ProtocolVersion);
            Assert.True(worker.Ready.RuntimeProfileApplied);
        }
        finally
        {
            // Best effort: a leaked temp directory is not worth failing a passing test over.
            try
            {
                Directory.Delete(staging, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    ///     The file names of every assembly listed under a <c>runtime</c> section in the worker's
    ///     dependency manifest.
    /// </summary>
    private static IEnumerable<string> DeclaredRuntimeAssemblies(string workerPath)
    {
        var depsPath = Path.ChangeExtension(workerPath, ".deps.json");

        Assert.True(File.Exists(depsPath), $"No dependency manifest beside the worker at '{depsPath}'.");

        using var document = JsonDocument.Parse(File.ReadAllBytes(depsPath));

        if (!document.RootElement.TryGetProperty("targets", out var targets))
            yield break;

        foreach (var target in targets.EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                if (!library.Value.TryGetProperty("runtime", out var runtime))
                    continue;

                foreach (var assembly in runtime.EnumerateObject())
                {
                    // Paths in the manifest are relative and, for a project reference, are bare file
                    // names. Reduced to the file name either way, because that is what the host looks
                    // for in the application directory.
                    yield return Path.GetFileName(assembly.Name);
                }
            }
        }
    }
}
