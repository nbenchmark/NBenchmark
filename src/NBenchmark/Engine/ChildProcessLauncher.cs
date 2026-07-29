using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using NBenchmark.Workers;

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

    /// <summary>
    ///     The OTLP endpoint the parent was told to export to (via <c>--otlp-endpoint</c> or
    ///     the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> env var). When set, the launcher
    ///     forwards it to every spawned child so isolated children stream their telemetry to
    ///     the same collector as the parent - this is the cross-process channel that the
    ///     in-memory <c>IBenchmarkProgress</c> callback cannot provide.
    /// </summary>
    internal const string OtelEndpointEnvVar = "NBENCHMARK_OTEL_ENDPOINT";

    private const int StdoutTailLines = 20;

    /// <summary>
    ///     Backstop ceiling for a child built without an options context. Generous, because
    ///     killing a legitimately slow benchmark is worse than waiting: the point of the
    ///     timeout is to bound a wedged child, not to enforce a budget.
    /// </summary>
    internal static readonly TimeSpan DefaultChildTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Floor for a <see cref="ComputeTimeout" />-derived budget, so a very small tuning
    ///     budget still leaves room for process start. Deliberately not applied to an explicitly
    ///     supplied timeout - see <see cref="Clamp" />.
    /// </summary>
    internal static readonly TimeSpan MinChildTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Absolute ceiling, applied to derived and explicit timeouts alike.</summary>
    internal static readonly TimeSpan MaxChildTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    ///     Fixed allowance for process start, JIT, discovery, and the user's entry-point
    ///     prologue - a child re-runs the whole of <c>Main</c>, which can do real work before
    ///     it reaches the benchmark.
    /// </summary>
    private static readonly TimeSpan ChildStartupAllowance = TimeSpan.FromSeconds(60);

    /// <summary>Per-benchmark slack for setup, teardown, and the between-benchmark full GC.</summary>
    private static readonly TimeSpan PerBenchmarkSlack = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     The OTel-standard env vars forwarded verbatim to children so the OpenTelemetry SDK
    ///     (when the user has wired one in their entry point) picks up the same exporter and
    ///     resource attributes in the child as in the parent. A child that re-runs the entry
    ///     assembly re-runs the user's SDK wiring, so these are the only values needed.
    /// </summary>
    private static readonly string[] OtelStandardEnvVars =
    [
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
        "OTEL_EXPORTER_OTLP_HEADERS",
        "OTEL_EXPORTER_OTLP_TIMEOUT",
        "OTEL_RESOURCE_ATTRIBUTES",
        "OTEL_SERVICE_NAME",
    ];

    /// <remarks>
    ///     Named floating-point literals are allowed because statistics legitimately produce
    ///     non-finite values - a benchmark whose samples are all identical has zero variance, so its
    ///     skewness and kurtosis are 0/0 - and <c>Utf8JsonWriter</c> otherwise throws rather than
    ///     writing <c>NaN</c>. Without this, a child that had measured perfectly well died while
    ///     serializing its own results.
    /// </remarks>
    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
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

    /// <summary>
    ///     Derives a child's wall-clock ceiling from the engine's own tuning budget, so the
    ///     timeout scales with what the child was actually asked to do and can never fire on a
    ///     benchmark that is merely slow.
    ///     <para>
    ///         <see cref="AutoTuneOptions.MaxTuningTime" /> times
    ///         <see cref="AutoTuneOptions.CapGraceFactor" /> is the engine's own hard ceiling on
    ///         in-body time per benchmark, so anything past that plus warmup and slack is a
    ///         wedged child rather than a busy one. <c>LaunchCount</c> is deliberately not a
    ///         factor: each launch is its own child process.
    ///     </para>
    /// </summary>
    internal static TimeSpan ComputeTimeout(MeasurementOptions options, int benchmarkCount)
        => MeasurementBudget.For(options, benchmarkCount);

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

        ApplyTelemetryEnvironment(psi, environmentVariables);

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

        ApplyTelemetryEnvironment(psi, environmentVariables);

        return psi;
    }

    /// <summary>
    ///     Applies the caller-supplied environment variables and the inherited OpenTelemetry
    ///     exporter/resource variables to <paramref name="psi" />. The OTel variables are
    ///     forwarded so an isolated child streams to the same collector as the parent - the
    ///     in-memory observer callback cannot cross the process boundary, so OTLP is the
    ///     cross-process channel. Caller-supplied variables win over the inherited OTel ones
    ///     so a caller can override the endpoint for a specific child if needed.
    /// </summary>
    /// <summary>
    ///     Applies a runtime profile to a child that has not started yet. This is the whole point
    ///     of spawning a process: tiering, PGO, ReadyToRun and GC flavour are read by the runtime
    ///     once at startup and cannot be changed afterwards, so the parent can only deliver them
    ///     here. A profile that inherits everything is a no-op and leaves the child's environment
    ///     untouched.
    ///     <para>
    ///         The profile name is also passed as a marker so the child can report by name what it
    ///         was launched under - the runtime offers no managed read-back for tiering, so the
    ///         child cannot work it out for itself.
    ///     </para>
    /// </summary>
    internal static void ApplyRuntimeProfile(ProcessStartInfo psi, RuntimeProfile? profile)
        => MeasurementBudget.ApplyRuntimeProfile(psi, profile);

    private static void ApplyTelemetryEnvironment(
        ProcessStartInfo psi,
        params (string Name, string Value)[] environmentVariables)
    {
        // Forward the OTel-standard exporter and resource variables first so caller-supplied
        // variables (added afterwards) can override any of them.
        foreach (var name in OtelStandardEnvVars)
        {
            var value = Environment.GetEnvironmentVariable(name);

            if (!string.IsNullOrEmpty(value))
                psi.Environment[name] = value;
        }

        // The NBenchmark-specific endpoint (set by --otlp-endpoint) is also mirrored into the
        // OTel-standard OTEL_EXPORTER_OTLP_ENDPOINT variable when the user has not set it
        // themselves, so an SDK wired only against the standard variable still picks it up.
        var nbenchmarkEndpoint = Environment.GetEnvironmentVariable(OtelEndpointEnvVar);

        if (!string.IsNullOrEmpty(nbenchmarkEndpoint)
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
            psi.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = nbenchmarkEndpoint;

        foreach (var (name, value) in environmentVariables)
        {
            psi.Environment[name] = value;
        }
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

    private static string BuildTimeoutMessage(
        IsolatedRunRequest request,
        TimeSpan timeout,
        StringBuilder stderr,
        Queue<string> stdoutTail)
    {
        var sb = new StringBuilder();

        sb.Append(
            $"Isolated child process for {Describe(request)} exceeded its {timeout.TotalSeconds:0.#}s timeout "
            + "and was killed. This usually means the benchmark body deadlocked, waited on I/O that never "
            + "completed, or the entry point blocked before reaching the benchmark. Raise the budget with "
            + "--max-tuning-time if the work is genuinely this slow.");

        if (stderr.Length > 0)
            sb.Append($" stderr: {stderr.ToString().Trim()}");

        if (stdoutTail.Count > 0)
            sb.Append($" stdout (last {stdoutTail.Count} lines): {string.Join(" | ", stdoutTail).Trim()}");

        return sb.ToString();
    }

    /// <summary>
    ///     Guards against a timeout that would disable the bound rather than set it. A
    ///     non-positive value - which includes <see cref="Timeout.InfiniteTimeSpan" /> (-1 ms) -
    ///     falls back to <see cref="DefaultChildTimeout" /> rather than firing instantly or
    ///     restoring the original unbounded wait; anything above the ceiling is capped.
    ///     <para>
    ///         A small positive value is honoured, so a caller can deliberately ask for a tight
    ///         bound. The 60-second floor belongs to <see cref="ComputeTimeout" />, which is
    ///         where a budget is being derived rather than chosen.
    ///     </para>
    /// </summary>
    private static TimeSpan Clamp(TimeSpan timeout)
        => timeout <= TimeSpan.Zero ? DefaultChildTimeout
            : timeout > MaxChildTimeout ? MaxChildTimeout
            : timeout;

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

                var startInfo = !string.IsNullOrEmpty(request.EntryAssemblyPath)
                    ? BuildStartInfo(
                        request.EntryAssemblyPath,
                        (RequestPathEnvVar, requestPath),
                        (OutputPathEnvVar, outputPath))
                    : BuildStartInfo(
                        (RequestPathEnvVar, requestPath),
                        (OutputPathEnvVar, outputPath));

                // The only place the runtime profile can be applied: these variables are read by
                // the runtime at startup, so they must be on the environment before Start().
                ApplyRuntimeProfile(startInfo, request.RuntimeProfile);

                using var process = new Process { StartInfo = startInfo };

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

                // Capture the id now: Process.Id can throw once the object is disposed, and the
                // reaper needs a stable handle for the whole lifetime of the child.
                var processId = process.Id;
                ChildProcessReaper.Track(processId, process);

                try
                {
                    process.BeginErrorReadLine();
                    process.BeginOutputReadLine();

                    var timeout = Clamp(request.Timeout);

                    using var timeoutCts = new CancellationTokenSource(timeout);

                    using var linkedCts = CancellationTokenSource
                        .CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

                    try
                    {
                        await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                            && !cancellationToken.IsCancellationRequested)
                    {
                        // The child is wedged. Take its whole tree down, then report which
                        // timeout fired - previously this waited forever with no diagnostic.
                        ChildProcessReaper.KillTree(process);

                        return ErroredItems(request, BuildTimeoutMessage(request, timeout, stderr, stdoutTail));
                    }
                    catch (OperationCanceledException)
                    {
                        // The caller cancelled. Kill the tree before the exception escapes,
                        // otherwise the child outlives us and keeps measuring against temp
                        // files the finally block is about to delete.
                        ChildProcessReaper.KillTree(process);
                        throw;
                    }

                    if (process.ExitCode != 0 || !File.Exists(outputPath))
                    {
                        return ErroredItems(
                            request,
                            BuildFailureMessage(request, process.ExitCode, outputPath, stderr, stdoutTail));
                    }

                    return await ReadPayloadAsync(outputPath, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ChildProcessReaper.Untrack(processId);
                }
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
