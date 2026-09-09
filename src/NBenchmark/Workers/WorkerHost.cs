using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     A live measurement worker, from the coordinator's side: the child process, its duplex pipe,
///     and what it reported about itself.
/// </summary>
[RequiresUnreferencedCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
[RequiresDynamicCode("Runs a benchmark through the worker protocol, which reflects over the body's closure and prepared state and moves both with the reflection-based JSON serializer.")]
internal sealed class WorkerHost : IAsyncDisposable
{
    /// <summary>
    ///     How long to wait for a worker to start and answer the handshake. Generous relative to the
    ///     65-77 ms measured for spawn-plus-handshake, because a cold file cache or an
    ///     antivirus-scanned first launch is far slower than a warm one, and killing a worker that
    ///     was merely slow to start would look like a flaky benchmark.
    /// </summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How many lines of stderr to keep from the <i>start</i> of the worker's output. A .NET crash
    ///     dump puts its diagnostic header - "Stack overflow.", "Repeated N times:" - first, so a
    ///     tail-only window loses exactly the line that says what happened.
    /// </summary>
    private const int StderrHeadLines = 20;

    /// <summary>
    ///     How many lines of stderr to keep from the <i>end</i>: the bottom of the stack dump, where the
    ///     repeating frames and the "Process is terminating" footer land.
    /// </summary>
    private const int StderrTailLines = 20;

    private readonly Process _process;

    // The channel owns both pipe streams and disposes them, so they are not held separately here.
    private readonly FrameChannel _channel;
    private readonly StderrBuffer _stderr;
    private readonly int _processId;
    private bool _disposed;

    private WorkerHost(Process process, FrameChannel channel, ReadyPayload ready, StderrBuffer stderr)
    {
        _process = process;
        _channel = channel;
        _processId = process.Id;
        Ready = ready;

        // The live buffer the stderr handler writes into, not a copy of it. Copying at construction
        // time - which is what this did first - captured only what the worker said before its
        // handshake, so anything it reported while dying was silently discarded. That is the exact
        // moment the output matters most.
        _stderr = stderr;
    }

    /// <summary>What the worker reported about the process it is - not what it was asked to be.</summary>
    public ReadyPayload Ready { get; }

    public FrameChannel Channel => _channel;

    public int ProcessId => _processId;

    /// <summary>
    ///     The worker's stderr as a first-N + last-N window, for diagnosing a worker that died. A
    ///     tail-only window loses the diagnostic header a .NET crash dump prints first.
    /// </summary>
    public string StderrTail
    {
        get
        {
            lock (_stderr)
            {
                return _stderr.ToString();
            }
        }
    }

    /// <summary>
    ///     How the worker process ended, for a fault message. A worker that vanishes with no stderr
    ///     is otherwise undiagnosable - the exit code distinguishes a crash from a clean exit from
    ///     something having killed it, and those have completely different causes.
    /// </summary>
    public string ExitDescription
    {
        get
        {
            try
            {
                if (!_process.HasExited)
                    return "the process is still running";

                var code = _process.ExitCode;

                // The common crash exits are a closed set per platform; naming them turns an
                // undiagnosable vanishing process into an actionable message. See ExitCodeDescription
                // for why the old "killed by signal {-code}" branch was inverted on both platforms.
                return ExitCodeDescription.Describe(code);
            }
            catch (Exception ex) when (ex is InvalidOperationException or SystemException)
            {
                return "exit status unavailable";
            }
        }
    }

    /// <summary>
    ///     Waits briefly for the worker to finish exiting, so a diagnostic composed afterwards sees a
    ///     settled exit code and a drained stderr rather than racing both.
    /// </summary>
    public async Task WaitForExitAsync(TimeSpan timeout)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
        {
            // Still running, or already reaped. Either way the caller reports what it can see.
        }
    }

    /// <summary>
    ///     Spawns a worker under <paramref name="profile" /> and completes the handshake.
    ///     <para>
    ///         Applying the profile here, to the environment block of a process that has not started,
    ///         is the entire purpose of the design. It cannot be done later, from inside the worker,
    ///         at any price.
    ///     </para>
    /// </summary>
    /// <param name="workerAssemblyPath">
    ///     Path to the worker executable (<c>nbworker</c>), located by <c>WorkerLocator</c>.
    /// </param>
    /// <param name="profile">The runtime profile to launch the worker under, or <c>null</c> for the ambient configuration.</param>
    /// <param name="cancellationToken">Cancels the handshake while it is in progress.</param>
    public static Task<WorkerHost> StartAsync(
        string workerAssemblyPath,
        RuntimeProfile? profile,
        CancellationToken cancellationToken)
        => StartAsync(workerAssemblyPath, profile, runtimeConfigPath: null, cancellationToken);

    /// <summary>
    ///     Spawns a worker under <paramref name="profile" />, optionally with a runtimeconfig other
    ///     than its own, and completes the handshake.
    /// </summary>
    /// <param name="runtimeConfigPath">
    ///     A config declaring shared frameworks the worker's own does not - see
    ///     <see cref="SharedFrameworkConfig" />. <c>null</c> for every target that needs nothing beyond
    ///     <c>Microsoft.NETCore.App</c>, which leaves the command line exactly as it was.
    /// </param>
    /// <param name="workerAssemblyPath">
    ///     Path to the worker executable (<c>nbworker</c>), located by <c>WorkerLocator</c>.
    /// </param>
    /// <param name="profile">The runtime profile to launch the worker under, or <c>null</c> for the ambient configuration.</param>
    /// <param name="cancellationToken">Cancels the handshake while it is in progress.</param>
    public static async Task<WorkerHost> StartAsync(
        string workerAssemblyPath,
        RuntimeProfile? profile,
        string? runtimeConfigPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerAssemblyPath);

        // Named rather than inherited, so the read below is genuinely cancellable on Windows. See
        // WorkerTransport for the whole of that argument.
        var inboundPipeName = WorkerTransport.NewPipeName();
        var outboundPipeName = WorkerTransport.NewPipeName();

        var toWorker = WorkerTransport.CreateServer(inboundPipeName, PipeDirection.Out);
        var fromWorker = WorkerTransport.CreateServer(outboundPipeName, PipeDirection.In);

        Process? process = null;

        try
        {
            var startInfo = new ProcessStartInfo(WorkerLocator.ResolveDotnetMuxer())
            {
                UseShellExecute = false,
                CreateNoWindow = true,

                // stdout is left attached to the coordinator's own so a benchmark body that writes
                // to the console still shows the developer its output. The protocol runs over its
                // own pipe, so unlike the previous file-based scheme there is no payload for the
                // body's console output to corrupt.
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };

            startInfo.ArgumentList.Add("exec");

            // Ahead of the worker path, because these are host options rather than arguments to the
            // application. The deps file is still resolved from the application path, so the worker's
            // own nbworker.deps.json continues to describe its dependencies.
            if (runtimeConfigPath is not null)
            {
                startInfo.ArgumentList.Add("--runtimeconfig");
                startInfo.ArgumentList.Add(runtimeConfigPath);
            }

            startInfo.ArgumentList.Add(workerAssemblyPath);
            startInfo.ArgumentList.Add(WorkerProtocol.InboundPipeArgument);
            startInfo.ArgumentList.Add(inboundPipeName);
            startInfo.ArgumentList.Add(WorkerProtocol.OutboundPipeArgument);
            startInfo.ArgumentList.Add(outboundPipeName);
            startInfo.ArgumentList.Add(WorkerProtocol.ParentProcessIdArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            MeasurementBudget.ApplyRuntimeProfile(startInfo, profile);
            MeasurementBudget.ApplyTelemetryEnvironment(startInfo);

            process = new Process { StartInfo = startInfo };

            var stderr = new StderrBuffer(StderrHeadLines, StderrTailLines);

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;

                lock (stderr)
                {
                    stderr.Add(e.Data);
                }
            };

            process.Start();
            ChildProcessReaper.Track(process.Id, process);
            process.BeginErrorReadLine();

            await ConnectAsync(toWorker, fromWorker, process, stderr, cancellationToken).ConfigureAwait(false);

            var channel = new FrameChannel(fromWorker, toWorker);
            var ready = await HandshakeAsync(channel, process, stderr, cancellationToken).ConfigureAwait(false);

            return new WorkerHost(process, channel, ready, stderr);
        }
        catch
        {
            if (process is not null)
            {
                ChildProcessReaper.Untrack(process.Id);
                ChildProcessReaper.KillTree(process);
                process.Dispose();
            }

            toWorker.Dispose();
            fromWorker.Dispose();

            throw;
        }
    }

    /// <summary>
    ///     Waits for the worker to connect to both pipes, or for it to die trying.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Both waits run together rather than one after the other. They are independent pipes and
    ///         the worker connects to them in its own order, so awaiting them in sequence would be
    ///         correct but would spend the deadline twice over on a worker that is merely slow.
    ///     </para>
    ///     <para>
    ///         The race against process exit is what the anonymous-pipe version got for free and this
    ///         one has to ask for. A worker that dies on startup - bad arguments, a missing shared
    ///         framework, a static initializer that throws - used to surface immediately, because the
    ///         inherited write handle died with it and the handshake read hit end-of-stream. A worker
    ///         that never connects to a named pipe produces no such signal, so without this the caller
    ///         would wait the full <see cref="HandshakeTimeout" /> and then report a timeout for what
    ///         is really a crash with an exit code and stderr worth printing.
    ///     </para>
    /// </remarks>
    /// <param name="toWorker">The coordinator-to-worker pipe, awaiting its client.</param>
    /// <param name="fromWorker">The worker-to-coordinator pipe, awaiting its client.</param>
    /// <param name="process">The worker, so its death can end the wait early.</param>
    /// <param name="stderr">The worker's captured stderr, for the diagnostic if it died.</param>
    /// <param name="cancellationToken">Cancels the wait while it is in progress.</param>
    private static async Task ConnectAsync(
        NamedPipeServerStream toWorker,
        NamedPipeServerStream fromWorker,
        Process process,
        StderrBuffer stderr,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(HandshakeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var connected = Task.WhenAll(
            toWorker.WaitForConnectionAsync(linked.Token),
            fromWorker.WaitForConnectionAsync(linked.Token));

        // Uncancelled deliberately. Cancelling this once the pipes are up would leave a cancelled
        // task nobody awaits, and the alternative costs nothing: an uncancelled wait completes
        // quietly whenever the worker eventually exits, which it will.
        var exited = process.WaitForExitAsync(CancellationToken.None);

        try
        {
            if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited)
            {
                // Nothing awaits the connect after this. It is still pending, and it will fault the
                // moment the caller's catch disposes both streams on the way out - so it has to be
                // observed here, or that fault surfaces later as an unobserved task exception in
                // whatever process the developer was benchmarking.
                Observe(connected);

                throw new WorkerStartException(
                    "The measurement worker exited before it connected to the coordinator "
                    + $"({ExitCodeDescription.Describe(process.ExitCode)}).{DescribeStderr(stderr)}");
            }

            // Both waits are inside this one, and awaiting it observes them.
            await connected.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw new WorkerStartException(
                $"The measurement worker did not connect within {HandshakeTimeout.TotalSeconds:0.#}s. "
                + "It is running but never reached its pipes, which means it is wedged before "
                + $"NBenchmark's own entry point.{DescribeStderr(stderr)}");
        }
    }

    /// <summary>
    ///     Marks a task nobody will await as observed, so its eventual failure does not reach
    ///     <see cref="TaskScheduler.UnobservedTaskException" />.
    /// </summary>
    /// <param name="task">The abandoned task.</param>
    private static void Observe(Task task)
        => _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static async Task<ReadyPayload> HandshakeAsync(
        FrameChannel channel,
        Process process,
        StderrBuffer stderr,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(HandshakeTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        await channel
            .WriteAsync(
                WorkerFrame.Of(new HandshakePayload
                {
                    ProtocolVersion = WorkerProtocol.Version,
                    ParentProcessId = Environment.ProcessId,
                }),
                linked.Token)
            .ConfigureAwait(false);

        WorkerFrame? frame;

        try
        {
            frame = await channel.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw new WorkerStartException(
                $"The measurement worker did not answer the handshake within "
                + $"{HandshakeTimeout.TotalSeconds:0.#}s.{DescribeStderr(stderr)}");
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            // A torn or unreadable Ready frame: the worker began answering the handshake and then
            // died, or wrote a frame the stream could not parse. <see cref="FrameChannel.ReadAsync" />
            // throws <see cref="EndOfStreamException" /> (an <see cref="IOException" />) when the pipe
            // dies mid-frame, <see cref="InvalidDataException" /> on a bad length prefix, and
            // <see cref="JsonException" /> on a corrupt payload. Without this catch the exception
            // escapes <see cref="StartAsync" /> as something other than a
            // <see cref="WorkerStartException" />, so <c>ProcessWorkerLauncher</c> - which catches only
            // <c>WorkerStartException</c> - lets it take down the whole benchmark program. A worker
            // that hard-crashes during startup is the reachable case: a static initializer or a
            // <c>[GlobalSetup]</c> that stack-overflows dies before the Ready frame is fully written.
            var cause = process.HasExited
                ? ExitCodeDescription.Describe(process.ExitCode)
                : "the process is still running";

            throw new WorkerStartException(
                $"The measurement worker died while answering the handshake ({cause}): "
                + $"{ex.GetType().Name}.{DescribeStderr(stderr)}");
        }

        if (frame is null)
        {
            // End-of-stream before a handshake means the worker died on startup. Its exit code and
            // stderr are the only evidence, so both go into the message - the cause named (W-47)
            // rather than a bare number, the same way the torn-frame branch above names it.
            var cause = process.HasExited
                ? ExitCodeDescription.Describe(process.ExitCode)
                : "still running";

            throw new WorkerStartException(
                $"The measurement worker exited before answering the handshake ({cause})"
                + $".{DescribeStderr(stderr)}");
        }

        if (frame.Kind == WorkerFrameKind.Fault && frame.Fault is not null)
            throw new WorkerStartException($"The measurement worker refused to start: {frame.Fault.Message}");

        if (frame.Kind != WorkerFrameKind.Ready || frame.Ready is null)
        {
            throw new WorkerStartException(
                $"The measurement worker answered the handshake with a {frame.Kind} frame.");
        }

        var ready = frame.Ready;

        if (ready.ProtocolVersion != WorkerProtocol.Version)
        {
            throw new WorkerStartException(
                $"Protocol version mismatch: this build speaks v{WorkerProtocol.Version}, the worker "
                + $"on disk speaks v{ready.ProtocolVersion}. The worker ships inside the NBenchmark "
                + "package, so this means a stale copy in the output directory - a clean rebuild "
                + "usually fixes it.");
        }

        var engineVersion = typeof(MeasurementOptions).Assembly.GetName().Version?.ToString() ?? "unknown";

        if (!string.Equals(ready.EngineVersion, engineVersion, StringComparison.Ordinal))
        {
            // Worth failing over: the worker unifies NBenchmark from its own load context rather
            // than the target's, so a skew means the measurement runs against different engine code
            // than the user compiled against, and nothing else would reveal it.
            throw new WorkerStartException(
                $"The measurement worker carries NBenchmark {ready.EngineVersion} but this process is "
                + $"{engineVersion}. Mixed versions would measure against different engine code than "
                + "you compiled against. Clean the output directory and rebuild.");
        }

        return ready;
    }

    private static string DescribeStderr(StderrBuffer stderr)
    {
        lock (stderr)
        {
            var window = stderr.ToString();

            return window.Length == 0
                ? ""
                : $" Worker stderr: {window.Replace(Environment.NewLine, " | ").Trim()}";
        }
    }

    /// <summary>
    ///     Drops this side of the connection without asking the worker to stop and without killing it -
    ///     what a worker sees when its coordinator dies outright (a crash, a <c>kill -9</c>, an IDE stop
    ///     button).
    /// </summary>
    /// <remarks>
    ///     Exists so that case is testable. The worker is expected to notice end-of-stream and exit on
    ///     its own, and it is left tracked by <see cref="ChildProcessReaper" /> so one that fails to
    ///     notice is still reaped. A subsequent <see cref="DisposeAsync" /> stays safe: its shutdown
    ///     write throws <see cref="ObjectDisposedException" />, which it already catches.
    /// </remarks>
    internal void Abandon()
    {
        try
        {
            _channel.Dispose();
        }
        catch (IOException)
        {
            // The worker tore its end down first.
        }
    }

    /// <summary>
    ///     Asks the worker to exit, then makes sure it did.
    ///     <para>
    ///         Closing the pipe alone would be enough - the worker reads its inbound pipe continuously,
    ///         so end-of-stream reaches it whether it is idle or measuring, and it exits on its own.
    ///         The explicit frame is the polite path so a worker mid-write finishes cleanly; the kill is
    ///         the backstop for one that is wedged in a benchmark body that never returns.
    ///     </para>
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _channel.WriteAsync(WorkerFrame.Shutdown(), shutdownCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Already gone, or not listening. The pipe close below is the real mechanism.
        }

        try
        {
            _channel.Dispose();
        }
        catch (IOException)
        {
            // The worker tore its end down first.
        }

        try
        {
            if (!_process.WaitForExit(2_000))
                ChildProcessReaper.KillTree(_process);
        }
        catch (InvalidOperationException)
        {
            // Already reaped.
        }
        finally
        {
            ChildProcessReaper.Untrack(_processId);
            _process.Dispose();
        }
    }
}
