using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     A live measurement worker, from the coordinator's side: the child process, its duplex pipe,
///     and what it reported about itself.
/// </summary>
internal sealed class WorkerHost : IAsyncDisposable
{
    /// <summary>
    ///     How long to wait for a worker to start and answer the handshake. Generous relative to the
    ///     65-77 ms measured for spawn-plus-handshake, because a cold file cache or an
    ///     antivirus-scanned first launch is far slower than a warm one, and killing a worker that
    ///     was merely slow to start would look like a flaky benchmark.
    /// </summary>
    internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private const int StderrTailLines = 20;

    private readonly Process _process;

    // The channel owns both pipe streams and disposes them, so they are not held separately here.
    private readonly FrameChannel _channel;
    private readonly Queue<string> _stderrTail;
    private readonly int _processId;
    private bool _disposed;

    private WorkerHost(Process process, FrameChannel channel, ReadyPayload ready, Queue<string> stderrTail)
    {
        _process = process;
        _channel = channel;
        _processId = process.Id;
        Ready = ready;

        // The live queue the stderr handler writes into, not a copy of it. Copying at construction
        // time - which is what this did first - captured only what the worker said before its
        // handshake, so anything it reported while dying was silently discarded. That is the exact
        // moment the output matters most.
        _stderrTail = stderrTail;
    }

    /// <summary>What the worker reported about the process it is - not what it was asked to be.</summary>
    public ReadyPayload Ready { get; }

    public FrameChannel Channel => _channel;

    public int ProcessId => _processId;

    /// <summary>The tail of the worker's stderr, for diagnosing a worker that died.</summary>
    public string StderrTail
    {
        get
        {
            lock (_stderrTail)
            {
                return string.Join(Environment.NewLine, _stderrTail);
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

                // A negative code on Unix is a signal: the process did not choose to exit.
                return code < 0
                    ? $"killed by signal {-code}"
                    : $"exit code {code}";
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
    public static async Task<WorkerHost> StartAsync(
        string workerAssemblyPath,
        RuntimeProfile? profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerAssemblyPath);

        var toWorker = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromWorker = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

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
            startInfo.ArgumentList.Add(workerAssemblyPath);
            startInfo.ArgumentList.Add(WorkerProtocol.InboundHandleArgument);
            startInfo.ArgumentList.Add(toWorker.GetClientHandleAsString());
            startInfo.ArgumentList.Add(WorkerProtocol.OutboundHandleArgument);
            startInfo.ArgumentList.Add(fromWorker.GetClientHandleAsString());
            startInfo.ArgumentList.Add(WorkerProtocol.ParentProcessIdArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            MeasurementBudget.ApplyRuntimeProfile(startInfo, profile);
            MeasurementBudget.ApplyTelemetryEnvironment(startInfo);

            process = new Process { StartInfo = startInfo };

            var stderrTail = new Queue<string>(StderrTailLines + 1);

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                    return;

                lock (stderrTail)
                {
                    stderrTail.Enqueue(e.Data);

                    if (stderrTail.Count > StderrTailLines)
                        stderrTail.Dequeue();
                }
            };

            process.Start();
            ChildProcessReaper.Track(process.Id, process);
            process.BeginErrorReadLine();

            // The parent's own copies must go, or the worker's exit is never visible as
            // end-of-stream and a read here would block forever on a dead process.
            toWorker.DisposeLocalCopyOfClientHandle();
            fromWorker.DisposeLocalCopyOfClientHandle();

            var channel = new FrameChannel(fromWorker, toWorker);
            var ready = await HandshakeAsync(channel, process, stderrTail, cancellationToken).ConfigureAwait(false);

            return new WorkerHost(process, channel, ready, stderrTail);
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

    private static async Task<ReadyPayload> HandshakeAsync(
        FrameChannel channel,
        Process process,
        Queue<string> stderrTail,
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
                + $"{HandshakeTimeout.TotalSeconds:0.#}s.{DescribeStderr(stderrTail)}");
        }

        if (frame is null)
        {
            // End-of-stream before a handshake means the worker died on startup. Its exit code and
            // stderr are the only evidence, so both go into the message.
            var exitCode = process.HasExited ? process.ExitCode.ToString() : "still running";

            throw new WorkerStartException(
                $"The measurement worker exited before answering the handshake (exit code "
                + $"{exitCode}).{DescribeStderr(stderrTail)}");
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

    private static string DescribeStderr(Queue<string> stderrTail)
    {
        lock (stderrTail)
        {
            return stderrTail.Count == 0
                ? ""
                : $" Worker stderr: {string.Join(" | ", stderrTail).Trim()}";
        }
    }

    /// <summary>
    ///     Asks the worker to exit, then makes sure it did.
    ///     <para>
    ///         Closing the pipe alone would be enough - the worker's blocking read returns
    ///         end-of-stream and it exits, measured at 7 ms. The explicit frame is the polite path so
    ///         a worker mid-write finishes cleanly; the kill is the backstop for one that is wedged
    ///         in a benchmark body that never returns.
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

/// <summary>
///     Thrown when a worker cannot be started or cannot be trusted. Callers treat this as "fall
///     back and say why", never as a reason to report a measurement.
/// </summary>
internal sealed class WorkerStartException(string message) : Exception(message);
