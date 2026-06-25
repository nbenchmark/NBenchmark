using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NBenchmark.Engine;

/// <summary>
///     The single child-process launcher shared by every isolated run. Both isolation
///     entry points - Suite mode's <c>WithIsolation()</c> and Harness mode's
///     isolated-by-default execution - funnel through here, so there is exactly one
///     process-launch path and one wire protocol.
///     <para>
///         The child re-runs the same entry assembly with no user arguments. The request
///         and output file paths travel via environment variables; the child reads the
///         request, runs the requested benchmarks in-process in its clean CLR, and writes
///         a serialized payload to the output file. Communication is file-based (never
///         stdout) so the child's own console output cannot corrupt the payload, and the
///         child never re-parses presentation flags (so a reporter assembly missing in the
///         child can never fail the run).
///     </para>
/// </summary>
internal static class ChildProcessLauncher
{
    internal const string RequestPathEnvVar = "NBENCHMARK_ISOLATED_REQUEST_PATH";
    internal const string OutputPathEnvVar = "NBENCHMARK_ISOLATED_OUTPUT_PATH";

    private const int StdoutTailLines = 20;

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    ///     The active launcher. Defaults to the real process-spawning implementation.
    ///     Tests replace this with a fake to avoid spawning real child processes.
    /// </summary>
    internal static IProcessLauncher Current { get; set; } = new DefaultLauncher();

    /// <summary>
    ///     Launches a child process for <paramref name="request" />, waits for it to
    ///     complete, and returns the result items it produced. Delegates to
    ///     <see cref="Current" /> so tests can swap the launcher.
    /// </summary>
    public static Task<IReadOnlyList<IsolatedResultItem>> LaunchAsync(
        IsolatedRunRequest request,
        CancellationToken cancellationToken)
        => Current.LaunchAsync(request, cancellationToken);

    internal static ProcessStartInfo BuildStartInfo(
        params (string Name, string Value)[] environmentVariables)
    {
        var processPath = Environment.ProcessPath;
        var entryAssembly = Assembly.GetEntryAssembly()?.Location;

        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        // For `dotnet myapp.dll` the host process is the muxer, so the managed entry
        // assembly must be passed as the first argument. For a native apphost
        // (`./myapp`) the executable runs the assembly directly.
        var isMuxer = processPath is null
                      || Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

        if (isMuxer)
        {
            psi.FileName = processPath ?? "dotnet";

            if (!string.IsNullOrEmpty(entryAssembly))
                psi.ArgumentList.Add(entryAssembly);
            else
            {
                throw new InvalidOperationException(
                    "Unable to resolve the managed entry assembly for isolated child execution. "
                    + "File-based program replay is not supported in this context.");
            }
        }
        else
            psi.FileName = processPath!;

        foreach (var (name, value) in environmentVariables)
        {
            psi.Environment[name] = value;
        }

        return psi;
    }

    /// <summary>
    ///     Builds a <see cref="ProcessStartInfo" /> that launches the specified entry assembly
    ///     via <c>dotnet exec</c>. Used for cross-runtime runs where the child must run a
    ///     specific TFM's build output rather than re-running the current process.
    /// </summary>
    internal static ProcessStartInfo BuildStartInfo(
        string entryAssemblyPath,
        params (string Name, string Value)[] environmentVariables)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(entryAssemblyPath);

        foreach (var (name, value) in environmentVariables)
        {
            psi.Environment[name] = value;
        }

        return psi;
    }

    public static async Task WritePayloadAsync(
        string outputPath,
        IReadOnlyList<IsolatedResultItem> items,
        CancellationToken cancellationToken)
    {
        var payload = new IsolatedPayload { Items = items };
        await using var stream = File.Create(outputPath);
        await JsonSerializer.SerializeAsync(stream, payload, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<IReadOnlyList<IsolatedResultItem>> ReadPayloadAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(outputPath);

        var payload = await JsonSerializer
            .DeserializeAsync<IsolatedPayload>(stream, SerializerOptions, cancellationToken)
            .ConfigureAwait(false);

        if (payload?.Items is null)
        {
            return
            [
                new IsolatedResultItem
                {
                    Result = ErroredResult("(unknown)", "Isolated child produced an unreadable result payload."),
                    RawSamples = [],
                },
            ];
        }

        return payload.Items;
    }

    public static async Task WriteRequestAsync(
        string requestPath,
        IsolatedRunRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(requestPath);
        await JsonSerializer.SerializeAsync(stream, request, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IsolatedRunRequest> ReadRequestAsync(string requestPath)
    {
        await using var stream = File.OpenRead(requestPath);

        var request = await JsonSerializer.DeserializeAsync<IsolatedRunRequest>(stream, SerializerOptions)
            .ConfigureAwait(false);

        if (request is null)
            throw new InvalidOperationException("Isolated request payload was unreadable.");

        return request;
    }

    private static IReadOnlyList<IsolatedResultItem> ErroredItems(IsolatedRunRequest request, string message)
    {
        var names = request.BenchmarkDisplayNames.Count > 0
            ? request.BenchmarkDisplayNames.Select(n => FullName(request.DisplayPrefix, n)).ToList()
            : [request.SuiteName ?? "(isolated)"];

        return names
            .Select(name => new IsolatedResultItem { Result = ErroredResult(name, message), RawSamples = [] })
            .ToList();
    }

    private static string BuildFailureMessage(
        IsolatedRunRequest request,
        int exitCode,
        string outputPath,
        StringBuilder stderr,
        Queue<string> stdoutTail)
    {
        var sb = new StringBuilder();
        sb.Append($"Isolated child process for {Describe(request)} exited with code {exitCode}.");

        if (!File.Exists(outputPath))
            sb.Append($" The child wrote no result payload to '{outputPath}'.");

        if (stderr.Length > 0)
            sb.Append($" stderr: {stderr.ToString().Trim()}");

        if (stdoutTail.Count > 0)
            sb.Append($" stdout (last {stdoutTail.Count} lines): {string.Join(" | ", stdoutTail).Trim()}");

        return sb.ToString();
    }

    private static string Describe(IsolatedRunRequest request)
        => request.Kind == IsolatedRunKind.Suite
            ? $"suite '{request.SuiteName}'"
            : $"class '{request.DeclaringTypeFullName}'";

    private static string FullName(string prefix, string name)
        => string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";

    private static BenchmarkResult ErroredResult(string name, string message) => new()
    {
        Name = name,
        Mean = 0,
        Median = 0,
        Percentiles = [],
        Min = 0,
        Max = 0,
        StandardDeviation = 0,
        Q1 = 0,
        Q3 = 0,
        InterquartileRange = 0,
        OutliersRemoved = 0,
        N = 0,
        Skewness = 0,
        Kurtosis = 0,
        Mad = 0,
        AllocMedian = null,
        AllocP95 = null,
        AllocMax = null,
        Errored = true,
        ErrorMessage = message,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a temp file; nothing actionable on failure.
        }
    }

    private sealed class DefaultLauncher : IProcessLauncher
    {
        public async Task<IReadOnlyList<IsolatedResultItem>> LaunchAsync(
            IsolatedRunRequest request,
            CancellationToken cancellationToken)
        {
            var requestPath = Path.Combine(Path.GetTempPath(), $"nbench-request-{Guid.NewGuid():N}.json");
            var outputPath = Path.Combine(Path.GetTempPath(), $"nbench-output-{Guid.NewGuid():N}.json");

            try
            {
                await WriteRequestAsync(requestPath, request, cancellationToken).ConfigureAwait(false);

                using var process = new Process
                {
                    StartInfo = !string.IsNullOrEmpty(request.EntryAssemblyPath)
                        ? BuildStartInfo(
                            request.EntryAssemblyPath,
                            (RequestPathEnvVar, requestPath),
                            (OutputPathEnvVar, outputPath))
                        : BuildStartInfo(
                            (RequestPathEnvVar, requestPath),
                            (OutputPathEnvVar, outputPath)),
                };

                var stderr = new StringBuilder();
                var stdoutTail = new Queue<string>(StdoutTailLines + 1);

                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data is not null)
                        stderr.AppendLine(e.Data);
                };

                // Keep only the tail of stdout: enough to diagnose a crash without buffering a
                // chatty child's entire output, while still draining the pipe so it cannot
                // fill and deadlock.
                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data is null)
                        return;

                    stdoutTail.Enqueue(e.Data);

                    if (stdoutTail.Count > StdoutTailLines)
                        stdoutTail.Dequeue();
                };

                process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                if (process.ExitCode != 0 || !File.Exists(outputPath))
                {
                    return ErroredItems(
                        request,
                        BuildFailureMessage(request, process.ExitCode, outputPath, stderr, stdoutTail));
                }

                return await ReadPayloadAsync(outputPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ErroredItems(
                    request, $"Failed to launch an isolated child process for {Describe(request)}: {ex.Message}");
            }
            finally
            {
                TryDelete(requestPath);
                TryDelete(outputPath);
            }
        }
    }
}
