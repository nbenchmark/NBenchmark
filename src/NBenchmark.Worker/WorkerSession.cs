using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using NBenchmark.Discovery;
using NBenchmark.Engine;
using NBenchmark.Stats;
using NBenchmark.Workers;

namespace NBenchmark.Worker;

/// <summary>
///     The worker's side of one connection: handshake, then serve run-group requests until the
///     coordinator says to stop or goes away.
/// </summary>
internal sealed class WorkerSession(FrameChannel channel)
{
    private readonly FrameChannel _channel = channel ?? throw new ArgumentNullException(nameof(channel));

    /// <summary>
    ///     Set for the duration of a group. Everything a group emits goes through the queue so the
    ///     measurement loop never waits on a pipe write; the handshake, which happens before any
    ///     measuring, is written directly.
    /// </summary>
    private FrameQueue? _queue;

    /// <summary>
    ///     The sample budget and seed for the group in flight, held here because every path that
    ///     produces results forwards them through <see cref="StreamingProgress.OnBenchmarkCompleted" />
    /// and none of the call sites carry the request that far.
    /// </summary>
    private int _maxRawSamples = MeasurementOptions.DefaultMaxRawSamples;

    private int _sampleSeed;

    /// <summary>
    ///     Cancelled once the coordinator's end of the pipe is provably gone. This is what the
    ///     measurement loop observes, so a group whose coordinator died stops at the next sample.
    /// </summary>
    /// <remarks>
    ///     Replaced per session rather than nullable, so no call site has to null-check it. Nothing
    ///     cancels it except <see cref="PumpInboundAsync" /> and a transport failure reported by
    ///     <see cref="FrameQueue" />.
    /// </remarks>
    private CancellationTokenSource _coordinatorLost = new();

    /// <summary>
    ///     Set when a group was abandoned mid-measurement, so the exit code can say the worker noticed
    ///     rather than that it finished.
    /// </summary>
    private volatile bool _abandonedMidGroup;

    /// <summary>
    ///     Set when the inbound stream became unreadable rather than merely ending, so the exit code
    ///     distinguishes a corrupt frame from a normal shutdown.
    /// </summary>
    /// <remarks>
    ///     Both end the pump and both close the channel, so without this they were indistinguishable
    ///     from here - and the shared path returned <see cref="WorkerExitCode.Success" />. A protocol
    ///     corruption exited 0 with an empty stderr, which is the least diagnosable outcome available
    ///     for a defect that is always a real one: a build skew, or a coordinator killed mid-frame.
    /// </remarks>
    private volatile string? _protocolError;

    /// <summary>
    ///     Runs until end-of-stream or a shutdown frame. Returns the process exit code.
    /// </summary>
    /// <remarks>
    ///     The <c>finally</c> is the flush point for anything the target's extension packages
    ///     activated - an OTLP exporter, most obviously. It runs on every way out of the session,
    ///     including the ones that report a lost coordinator, which is worth having: a worker that
    ///     measured successfully and then lost its parent still holds telemetry worth shipping.
    ///     Only an outright kill skips it.
    /// </remarks>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await RunSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ExtensionLoader.Deactivate();
        }
    }

    private async Task<int> RunSessionAsync(CancellationToken cancellationToken)
    {
        var handshake = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);

        if (handshake is null)
            return WorkerExitCode.NoHandshake;

        if (handshake.Kind != WorkerFrameKind.Handshake || handshake.Handshake is null)
        {
            Fault($"Expected a {nameof(WorkerFrameKind.Handshake)} frame first, got {handshake.Kind}.");

            return WorkerExitCode.ProtocolError;
        }

        if (handshake.Handshake.ProtocolVersion != WorkerProtocol.Version)
        {
            Fault(
                $"Protocol version mismatch: the coordinator speaks v{handshake.Handshake.ProtocolVersion}, "
                + $"this worker speaks v{WorkerProtocol.Version}. This means a stale nbworker on disk - "
                + "the worker ships inside the NBenchmark package and should always match.");

            return WorkerExitCode.ProtocolError;
        }

        await _channel.WriteAsync(WorkerFrame.Of(BuildReady()), cancellationToken).ConfigureAwait(false);

        using var coordinatorLost = new CancellationTokenSource();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, coordinatorLost.Token);

        _coordinatorLost = coordinatorLost;

        var inbound = Channel.CreateUnbounded<WorkerFrame>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        // Deliberately not awaited on the way out. A pending pipe read is not reliably interruptible,
        // and the process is exiting anyway; the pump owns the channel's read side for the rest of the
        // session, so there is no second reader to desynchronize the stream.
        _ = PumpInboundAsync(inbound.Writer, coordinatorLost, cancellationToken);

        while (true)
        {
            WorkerFrame frame;

            try
            {
                frame = await inbound.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // End of stream. The coordinator closed its write end - deliberately, because it died,
                // or because what arrived could not be read at all. Either way this worker has nothing
                // left to serve and no reason to linger.
                //
                // Said out loud because from the coordinator's side this is indistinguishable from a
                // crash: its next read returns end-of-stream either way. Without this line a worker
                // that exited for a perfectly ordinary reason looks like a lost process.
                if (_protocolError is { } corruption)
                {
                    Console.Error.WriteLine(
                        $"nbworker: the inbound stream could not be read ({corruption}); exiting. This is "
                        + "a protocol or build-skew problem rather than a normal shutdown - the worker "
                        + "ships inside the NBenchmark package and should always match the coordinator.");

                    return WorkerExitCode.ProtocolError;
                }

                Console.Error.WriteLine(
                    _abandonedMidGroup
                        ? "nbworker: coordinator went away mid-group; stopped measuring and exiting."
                        : "nbworker: inbound stream closed while idle; exiting.");

                return _abandonedMidGroup ? WorkerExitCode.CoordinatorLost : WorkerExitCode.Success;
            }

            switch (frame.Kind)
            {
                case WorkerFrameKind.Shutdown:
                    return WorkerExitCode.Success;

                case WorkerFrameKind.RunGroup when frame.RunGroup is not null:
                    await RunGroupAsync(frame.RunGroup, linked.Token).ConfigureAwait(false);
                    break;

                default:
                    Fault($"Unexpected frame {frame.Kind} while idle.");
                    break;
            }
        }
    }

    /// <summary>
    ///     Reads the inbound pipe for the whole life of the session - including while a group is being
    ///     measured, which is the entire point.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         End-of-stream is the signal the orphan-avoidance design rests on, and a read-then-dispatch
    ///         loop suspended it for exactly the interval that matters. Dispatch awaited
    ///         <see cref="RunGroupAsync" />, so nothing read the pipe during a group: a coordinator that
    ///         died mid-group was not noticed until the group's terminal write, and the worker measured
    ///         the whole remaining group - plus a calibration it could never report - for nobody. The
    ///         "exits on its own, measured at 7 ms" property was true only while idle.
    ///     </para>
    ///     <para>
    ///         Treating end-of-stream as fatal is safe here because no coordinator path closes its write
    ///         end early: <c>WorkerGroupRunner</c> holds the channel open for the whole group and only
    ///         reads, and on Ctrl-C the coordinator's <c>ChildProcessReaper</c> kills tracked workers
    ///         rather than politely orphaning them. So a mid-group end-of-stream means the coordinator
    ///         died hard, and cancelling cannot truncate a result anybody could still receive - the pipe
    ///         that would have carried it is the thing that closed.
    ///     </para>
    /// </remarks>
    private async Task PumpInboundAsync(
        ChannelWriter<WorkerFrame> writer,
        CancellationTokenSource coordinatorLost,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);

                if (frame is null)
                    break;

                // A shutdown that arrives mid-group is the coordinator saying it is finished, which is
                // strictly earlier than the pipe close on the polite teardown path. Stop measuring now
                // rather than finishing a group whose results are already unwanted.
                if (frame.Kind == WorkerFrameKind.Shutdown && _queue is not null)
                    SignalCoordinatorLost(coordinatorLost);

                await writer.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            // The stream desynchronized, or a payload arrived that is not a frame. Separated from the
            // benign endings below because it is never benign: something wrote bytes this worker cannot
            // parse, and folding it in with "the pipe closed" reported a defect as a clean exit.
            //
            // The coordinator's own read loop already treats these two as a torn frame
            // (WorkerGroupRunner catches JsonException and InvalidDataException); this is the same
            // judgement at the other end of the same transport.
            _protocolError = $"{ex.GetType().Name}: {ex.Message}";
        }
        catch (Exception ex) when (ex is IOException
                                       or ObjectDisposedException
                                       or OperationCanceledException)
        {
            // The pipe broke or the session is shutting down. Either way there is nothing more to read
            // and the finally below is the whole response.
        }
        finally
        {
            // Cancel *before* completing the writer. The measurement loop has to learn the coordinator
            // is gone even though the dispatch loop is not currently waiting on a frame - completing
            // first would wake only the dispatch loop, which is blocked behind the group.
            SignalCoordinatorLost(coordinatorLost);

            writer.TryComplete();
        }
    }

    /// <summary>
    ///     Cancels the coordinator-lost source, tolerating one that has already been disposed.
    /// </summary>
    /// <remarks>
    ///     The source's lifetime is <see cref="RunAsync" />'s, and both callers can outlive it. The pump
    ///     is deliberately not awaited on the way out, so a clean shutdown disposes the source and then
    ///     the pump's next read fails and reaches its <c>finally</c>; a queued frame's write can fail
    ///     after the session has already returned. Neither is worth an unobserved exception on a
    ///     fire-and-forget task, and by that point there is nothing left to cancel anyway.
    /// </remarks>
    private static void SignalCoordinatorLost(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session already finished and tore it down.
        }
    }

    /// <summary>
    ///     Reports what this process is actually running under. Every field is read from the
    ///     worker's own state, never echoed from the request: the coordinator needs to know what is
    ///     true of the measuring process, not what it asked for.
    /// </summary>
    private static ReadyPayload BuildReady()
    {
        var captured = RuntimeProfileEnvironment.Current;

        return new ReadyPayload
        {
            ProtocolVersion = WorkerProtocol.Version,
            WorkerProcessId = Environment.ProcessId,
            RuntimeProfileName = captured.Name,
            RuntimeKnobs = captured.Knobs,
            RuntimeProfileApplied = captured.WasApplied,
            TargetFramework = WorkerRuntime.TargetFramework,
            EngineVersion = WorkerRuntime.EngineVersion,
            ProcessArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        };
    }

    private async Task RunGroupAsync(RunGroupPayload request, CancellationToken cancellationToken)
    {
        // Held outside the try so the terminal flush can reach it on the failure path too: a group
        // that faulted mid-measurement has already streamed samples, and leaving them in the buffer
        // would drop the events leading up to whatever went wrong - the ones worth having.
        StreamingProgress? progress = null;

        // Held outside the try for the same reason, and a sharper one. A `using var` inside the try
        // disposes at the end of *that block*, which is before the finally runs - so affinity and
        // priority were restored before MeasureCalibration() measured the standard a test gate
        // divides by. The numerator was pinned and the denominator was not, and the ratio absorbed
        // the difference.
        IDisposable? environment = null;

        // The thread-scoped half of the same idea, and held outside the try for the same reason -
        // the calibration standard has to be measured under the same thread placement as the
        // benchmarks it is divided into.
        IDisposable? threadEnvironment = null;

        try
        {
            // One load context for the whole group. Not an optimisation: a second context loads a
            // second copy of the target assembly, and the same logical type from two contexts is two
            // distinct Type identities. That broke the service-provider factory outright - the worker
            // built a container registering the type from one context and then asked it for the type
            // from the other, which is simply not registered. Anything that resolves user code from a
            // request has to do it through the same context that discovery used.
            var context = new BenchmarkLoadContext(request.TargetAssemblyPath);

            // Built here rather than inside the Lambdas path, because a factory can carry captures
            // too and a strategy factory is resolved on the next line. One table for the group is also
            // the point: a prepare delegate and a body closing over the same local have to be handed
            // one object, exactly as two bodies sharing a display class are.
            var receivers = new ResolvedReceivers(request.Receivers, context);

            // Collected rather than printed. A substitution here changes how every number in the group
            // should be read, and it has to reach the rows themselves - see StreamingProgress.
            var substitutions = new List<string>();

            var options = ResolveStrategies(request, context, receivers, substitutions);

            _maxRawSamples = options.MaxRawSamples;

            // The run's own seed when it has one, so the same seed ships the same subset. Groups
            // without a seed still need a fixed value rather than a time-derived one, or two runs of
            // an identical configuration would disagree about which samples they sent.
            _sampleSeed = request.Seed ?? 0;

            // The worker was launched with the runtime profile already applied to its environment
            // block - that is the only moment it could have been. Affinity and priority, by
            // contrast, are settable at any time and belong here.
            environment = EnvironmentControl.Apply(options.Environment, options.SuppressedWarnings);

            // Applied to *this* thread, which is the one the measurement loop runs on: the group's
            // work is awaited from here, and the loop itself is straight-line synchronous code. A
            // process affinity does not stop the runtime's own threads from sharing the pinned
            // core, and on macOS the process call does not exist - the quality-of-service class
            // that decides performance- versus efficiency-core placement is settable only on the
            // calling thread, which is what makes this a sibling scope rather than part of the one
            // above.
            threadEnvironment = ThreadEnvironmentControl.Apply(options.Environment);

            // The failure callback is a second, independent route to the same conclusion: the pump sees
            // the inbound half break, this sees the outbound half. Either one means there is no
            // coordinator left to measure for.
            _queue = new FrameQueue(_channel, cancellationToken, OnTransportFailure);
            progress = new StreamingProgress(
                _queue,
                cancellationToken,
                options.StreamSamples,
                _maxRawSamples,
                _sampleSeed,
                substitutions);

            switch (request.Kind)
            {
                case WorkGroupKind.DiscoveredClass:
                    await RunDiscoveredClassAsync(request, context, options, receivers, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkGroupKind.Lambdas:
                    await RunLambdasAsync(request, context, options, receivers, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkGroupKind.Plan:
                    await RunPlanAsync(request, context, receivers, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkGroupKind.TestMethod:
                    await RunTestMethodAsync(request, context, options, progress, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    Fault($"Group kind {request.Kind} is not supported by this worker.");
                    break;
            }
        }
        catch (OperationCanceledException) when (_coordinatorLost.IsCancellationRequested)
        {
            // Abandoned, not cancelled by the session. Swallowed rather than rethrown: the dispatch
            // loop ends on its own when the pump completes the frame channel, and rethrowing would
            // surface a deliberate stop as an unhandled failure in Program's last-resort handler.
            _abandonedMidGroup = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is FileNotFoundException
                                       or FileLoadException
                                       or ReflectionTypeLoadException)
        {
            // The one failure shape that is about the worker's own process rather than the
            // benchmark. It reads as an ordinary missing-file error, which sends the reader looking
            // for a file that is not missing - it is present in a shared framework this process was
            // not started with. The coordinator normally prevents that by extending the worker's
            // framework set from the target's runtimeconfig.json, so if it happened, that file is
            // the thing to look at.
            Fault(
                $"The group failed: {ex.Message} The worker is running on "
                + $"{RuntimeInformation.FrameworkDescription} with frameworks "
                + $"[{string.Join(", ", LoadedSharedFrameworks())}]. If the assembly under test "
                + "targets a shared framework this list does not name - ASP.NET Core and Windows "
                + "Desktop are the usual ones - its runtimeconfig.json is how the worker learns to "
                + "ask for it, so check that the file is present and current beside the assembly, "
                + "and rebuild if it is not.",
                ex.ToString());
        }
        catch (Exception ex)
        {
            Fault($"The group failed: {ex.Message}", ex.ToString());
        }
        finally
        {
            if (_coordinatorLost.IsCancellationRequested)
            {
                // Nothing below can be delivered, so none of it is worth doing. Draining would wait on
                // writes that cannot land, and the calibration is a full measurement pass spent on a
                // number with no reader - which is precisely the cost this whole path exists to avoid.
                _abandonedMidGroup = true;
            }
            else
            {
                // Into the queue first, then drain it: a batch still in the sample buffer has not been
                // enqueued at all, so draining without flushing would lose it rather than wait for it.
                progress?.FlushSamples();

                // Drain before the terminal frame, so the coordinator never sees a group complete
                // while that group's own events are still in flight behind it.
                if (_queue is not null)
                    await _queue.DrainAsync().ConfigureAwait(false);

                // Measured here, after the group's work, so it reflects the process the benchmark
                // actually ran in rather than a freshly-started one - which includes its affinity and
                // priority, so the environment scope is still open at this point and is disposed
                // below. Its whole purpose is to be divisible into that benchmark's number.
                var calibration = request.MeasureCalibration ? MeasureCalibration() : null;

                try
                {
                    // CancellationToken.None, not the group's token: this runs in a finally, and a
                    // cancelled write here would throw out of it and be reported as a crash rather
                    // than as the deliberate stop it is.
                    await _channel
                        .WriteAsync(
                            WorkerFrame.Of(new GroupCompletedPayload
                            {
                                GroupId = request.GroupId,
                                Calibration = calibration,
                            }),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // The coordinator went away between the last frame and this one.
                    _abandonedMidGroup = true;
                }
            }

            // Last, after the calibration it has to cover. Restoring affinity and priority is the end
            // of the group in the most literal sense, and doing it any earlier is what left the
            // standard measured under a different process configuration than the benchmark it judges.
            // The thread scope goes first, innermost-last-opened, and restores nothing if the finally
            // arrived on a different thread than the one it pinned.
            threadEnvironment?.Dispose();
            environment?.Dispose();

            _queue = null;
        }
    }

    /// <summary>
    ///     Called once when an outbound write proves the coordinator can no longer be reached.
    /// </summary>
    private void OnTransportFailure() => SignalCoordinatorLost(_coordinatorLost);

    /// <summary>
    ///     Measures the methods of a test-framework group - methods with no <c>[Benchmark]</c>
    ///     attribute, handed over by an xUnit, NUnit or MSTest integration.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The test-class instance is built here rather than sent, which is why the coordinator only
    ///         routes classes it has already confirmed are constructible from nothing. Everything after
    ///         the methods are resolved is the ordinary discovery path, so instance lifetime, iteration
    ///         structure and sample transport are the same code that measures a <c>[Benchmark]</c>.
    ///     </para>
    ///     <para>
    ///         A group carries more than one method when the test names a reference to compare against.
    ///         They are measured in one suite here, in this process, so the per-replicate ratio the
    ///         coordinator forms is paired. Splitting them across two workers is the thing that makes a
    ///         test-integration ratio describe the two processes instead of the two bodies.
    ///     </para>
    /// </remarks>
    private async Task RunTestMethodAsync(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        MeasurementOptions options,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        if (request.TestMethods.Count == 0)
        {
            Fault("A test-method group carried no methods to measure.");

            return;
        }

        var target = context.LoadFromAssemblyPath(Path.GetFullPath(request.TargetAssemblyPath));

        ExtensionLoader.ActivateExtensions(context, target);

        var module = target.ManifestModule;

        if (module.ModuleVersionId != request.TestMethodModuleVersionId)
        {
            Fault(
                $"'{Path.GetFileName(request.TargetAssemblyPath)}' in this worker is a different build "
                + "from the one the test host addressed, so the method token cannot be trusted. "
                + "Rebuild and re-run.");

            return;
        }

        var resolvedMethods = new List<(MethodInfo Method, object?[] Arguments, string DisplayName)>(
            request.TestMethods.Count);

        foreach (var requested in request.TestMethods)
        {
            if (!TryResolveTestMethod(module, context, requested, out var method, out var arguments))
                return;

            resolvedMethods.Add((method!, arguments!, requested.DisplayName));
        }

        var suite = BenchmarkDiscoverer.DefineExplicit(resolvedMethods);

        // Discovery names a result '<Class>.<DisplayName>', which is the right convention for a
        // benchmark class and the wrong one here - the caller's display name is already qualified
        // however its test framework qualifies names. The rename is set on the progress sink before
        // the group runs, so each result is renamed as it completes and is sent over the wire with
        // the caller's name - there is no later list to walk, because W-44 sends incrementally as
        // each benchmark finishes rather than batching to group end.
        var simplePrefix = suite.Type.Name;
        var qualifiedPrefix = suite.Type.FullName ?? suite.Type.Name;

        var requestedNameByMeasuredName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (_, _, displayName) in resolvedMethods)
        {
            // Discovery historically emitted "<TypeName>.<DisplayName>" and now emits
            // "<TypeFullName>.<DisplayName>" for discovered paths. Test-method callers own the
            // display name, so map either form back to the caller-provided name.
            requestedNameByMeasuredName[$"{simplePrefix}.{displayName}"] = displayName;
            requestedNameByMeasuredName[$"{qualifiedPrefix}.{displayName}"] = displayName;
            requestedNameByMeasuredName[displayName] = displayName;
        }

        progress.SetResultRename(
            name => requestedNameByMeasuredName.TryGetValue(name, out var requestedName)
                ? requestedName
                : name);

        var outcome = await DiscoveredGroupExecutor.RunAsync(
                suite,
                suite.Benchmarks,
                options,
                instanceFactory: null,
                request.Order,
                request.Seed,
                request.StartIndex,
                request.TotalBenchmarks,
                progress,
                progress.AsObserver(),
                postSuiteCleanup: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.InstantiationFailed)
        {
            // The coordinator classified this class as constructible before routing it here, so
            // reaching this means the two disagreed. Reporting it is the only honest option: a
            // silent fallback would measure nothing and report a gap.
            Fault(
                $"'{suite.Type.FullName}' could not be instantiated in the worker, even "
                + "though it appeared to have a usable parameterless constructor."
                + (outcome.Failure is { Length: > 0 } why ? $" {why}" : ""));

            return;
        }

        // Results were sent incrementally as each benchmark completed (above, via
        // progress.OnBenchmarkCompleted), so there is nothing to batch here.
    }

    /// <summary>
    ///     Closes a resolved test method over the type arguments the request carried.
    /// </summary>
    /// <remarks>
    ///     The declaring type first, then the method: closing the type re-resolves the method from its
    ///     handle against the closed type, so a method closure applied earlier would be discarded. Both
    ///     steps are no-ops for the non-generic case, which is nearly every test.
    /// </remarks>
    private static bool TryCloseTestMethod(
        BenchmarkLoadContext context,
        TestMethodPayload requested,
        ref MethodInfo method,
        out string? error)
    {
        error = null;

        if (method.DeclaringType is { IsGenericTypeDefinition: true } definition)
        {
            if (requested.TypeGenericArguments is not { Count: > 0 } typeNames)
            {
                error = $"'{definition.Name}' is generic but the request carries no type arguments for it.";

                return false;
            }

            if (!GenericArguments.TryResolve(
                    typeNames, name => TypeNames.Resolve(name, context), out var typeArguments, out var unresolved))
            {
                error = $"Type argument '{unresolved}' could not be resolved in this worker.";

                return false;
            }

            try
            {
                var closed = definition.MakeGenericType(typeArguments);

                method = (MethodInfo)MethodBase.GetMethodFromHandle(method.MethodHandle, closed.TypeHandle)!;
            }
            catch (Exception ex) when (ex is ArgumentException or TypeLoadException)
            {
                error = $"'{definition.Name}' could not be closed over the carried type arguments: {ex.Message}";

                return false;
            }
        }

        if (!method.IsGenericMethodDefinition)
            return true;

        if (requested.MethodGenericArguments is not { Count: > 0 } methodNames)
        {
            error = $"'{method.Name}' is a generic method but the request carries no type arguments for it.";

            return false;
        }

        if (!GenericArguments.TryResolve(
                methodNames, name => TypeNames.Resolve(name, context), out var methodArguments, out var missing))
        {
            error = $"Type argument '{missing}' could not be resolved in this worker.";

            return false;
        }

        try
        {
            method = method.MakeGenericMethod(methodArguments);
        }
        catch (Exception ex) when (ex is ArgumentException or TypeLoadException)
        {
            error = $"'{method.Name}' could not be closed over the carried type arguments: {ex.Message}";

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Resolves one addressed test method and rebuilds its arguments, faulting the group when
    ///     either cannot be done.
    /// </summary>
    private bool TryResolveTestMethod(
        Module module,
        BenchmarkLoadContext context,
        TestMethodPayload requested,
        out MethodInfo? method,
        out object?[]? arguments)
    {
        method = null;
        arguments = null;

        try
        {
            if (module.ResolveMethod(requested.Token) is not MethodInfo resolved)
            {
                Fault($"Token 0x{requested.Token:X8} did not resolve to a method.");

                return false;
            }

            method = resolved;
        }
        catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
        {
            Fault($"Token 0x{requested.Token:X8} could not be resolved: {ex.Message}");

            return false;
        }

        // A token names the open definition, so a test on a closed generic class or a closed generic
        // method arrives here as something that cannot be invoked. The declaring type is closed first,
        // because doing so re-resolves the method against it.
        if (!TryCloseTestMethod(context, requested, ref method, out var closeError))
        {
            Fault(closeError!);

            return false;
        }

        var parameters = method.GetParameters();

        if (parameters.Length != requested.Arguments.Count)
        {
            Fault(
                $"'{method.Name}' takes {parameters.Length} argument(s) but {requested.Arguments.Count} "
                + "were sent. This usually means the assembly was rebuilt between addressing and running.");

            return false;
        }

        var decoded = new object?[parameters.Length];

        try
        {
            for (var i = 0; i < parameters.Length; i++)
            {
                decoded[i] = TestArgumentCodec.Decode(requested.Arguments[i], parameters[i].ParameterType);
            }
        }
        catch (Exception ex)
        {
            Fault($"An argument for '{method.Name}' could not be rebuilt in this worker: {ex.Message}");

            return false;
        }

        arguments = decoded;

        return true;
    }

    /// <summary>
    ///     Builds the instance factory for a discovered-class group from the addressed service-provider
    ///     factory, or leaves it null when the group resolves its own instances.
    /// </summary>
    /// <remarks>
    ///     A failure here is a <b>fault</b>, not a fallback. Everywhere else in this file an unusable
    ///     addressed delegate degrades to a built-in - a statistical strategy has a sensible default. A
    ///     service provider has none: constructing the benchmark type directly instead would produce an
    ///     object with none of its dependencies configured, measure it, and report it under the name the
    ///     caller asked for. That is the exact substitution the whole design refuses.
    /// </remarks>
    private bool TryBuildInstanceFactory(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        ResolvedReceivers receivers,
        out Func<Type, InstanceHandle>? instanceFactory)
    {
        instanceFactory = null;

        if (request.InstanceSource is not { } source)
            return true;

        if (source.Kind == InstanceSourceKind.InstanceFactory)
            return TryBuildFromInstanceFactory(request, context, source, receivers, out instanceFactory);

        if (!FactoryResolver.TryInvoke<IServiceProvider>(
                context,
                request.TargetAssemblyPath,
                source.Factory,
                receivers,
                out var provider,
                out var error,
                out var detail))
        {
            Fault(Capitalize(error!), detail);

            return false;
        }

        if (source.Kind == InstanceSourceKind.ScopedServiceProvider)
        {
            // Scoped registrations resolved off the root are the failure this kind exists to prevent:
            // under ValidateScopes the container throws, and without it every benchmark method shares
            // one DbContext - and its warmed change tracker - which is exactly the dependence the
            // significance test assumes is absent.
            if (!ServiceScopes.TryCreateScopedResolver(context, provider, out instanceFactory, out var scopeError))
            {
                Fault($"{Capitalize(source.Factory.Role)} produced a container, but {scopeError}");

                return false;
            }

            return true;
        }

        instanceFactory = type =>
        {
            var instance = provider.GetService(type)
                           ?? throw new InvalidOperationException(
                               $"No service of type '{type.FullName}' is registered in the service "
                               + "provider built by the factory. The worker builds its own container "
                               + "from your factory, so a registration added outside it is not present.");

            return InstanceHandle.NoTeardown(instance);
        };

        return true;
    }

    /// <summary>
    ///     Builds the instance factory by invoking the user's own addressed
    ///     <c>Func&lt;Type, object&gt;</c>, once per instance, with the benchmark class as argument.
    /// </summary>
    /// <remarks>
    ///     Resolution is deferred to the moment an instance is wanted rather than done once here,
    ///     because unlike a container the factory has nothing to build up front - and a failure to
    ///     address it must fault the group before any measuring starts, which is what the eager check
    ///     below is for.
    /// </remarks>
    private bool TryBuildFromInstanceFactory(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        InstanceSourcePayload source,
        ResolvedReceivers receivers,
        out Func<Type, InstanceHandle>? instanceFactory)
    {
        instanceFactory = null;

        // Bound once, up front, so an unusable address faults the group before any measuring starts -
        // and without invoking the factory, which would build an object nobody asked for.
        if (!FactoryResolver.TryBind(
                context,
                request.TargetAssemblyPath,
                source.Factory,
                receivers,
                typeof(object),
                arity: 1,
                out var invoke,
                out var error))
        {
            Fault(Capitalize(error!));

            return false;
        }

        instanceFactory = type =>
        {
            // Left to throw. BenchmarkLifecycle.CreateInstance catches it and turns it into the
            // errored rows the user reads, and the factory's own exception is more useful there than
            // anything this could wrap it in.
            var instance = invoke([type])
                           ?? throw new InvalidOperationException(
                               $"{source.Factory.Role} returned null for '{type.FullName}'.");

            return InstanceHandle.NoTeardown(instance);
        };

        return true;
    }

    private async Task RunDiscoveredClassAsync(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        MeasurementOptions options,
        ResolvedReceivers receivers,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        var target = context.LoadFromAssemblyPath(Path.GetFullPath(request.TargetAssemblyPath));

        ExtensionLoader.ActivateExtensions(context, target);

        // A container built here, from the caller's own registrations, rather than a live one sent
        // across - which is impossible - or a parameterless constructor substituted for it, which would
        // measure a differently-configured object under the right name. Absent a factory this stays
        // null and the group was never routed here in the first place.
        //
        // Ahead of discovery, not after it: discovery invokes [ArgumentsSource] sources, and whether
        // instances come from a factory decides whether an *instance* source may be invoked at all.
        if (!TryBuildInstanceFactory(request, context, receivers, out var instanceFactory))
            return;

        // Restricted to the class this group is about. A whole-assembly pass invokes every class's
        // [ArgumentsSource] source, so an N-class assembly measured one class per group ran all N
        // sources - and their side effects - N times over, to use one of them.
        var discoverer = new BenchmarkDiscoverer(
            request.DefaultInstanceLifetime,
            factoryResolvedInstances: request.InstanceSource is not null);

        IReadOnlyList<BenchmarkSuiteDefinition> suites;

        try
        {
            suites = discoverer.Discover(target, request.DeclaringTypeFullName);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TargetInvocationException)
        {
            // Discovery throws for a malformed benchmark - a case source that cannot be invoked, an
            // arity mismatch, an instance source on a factory-resolved class. Reported as this group's
            // fault, because the alternative is an unhandled exception in the worker and a coordinator
            // that sees only a process that vanished.
            Fault(ex.Message);

            return;
        }

        var suite = suites.FirstOrDefault(s => s.Type.FullName == request.DeclaringTypeFullName);

        if (suite is null)
        {
            Fault(
                $"Class '{request.DeclaringTypeFullName}' was not found in "
                + $"'{Path.GetFileName(request.TargetAssemblyPath)}'. If the class was renamed or "
                + "removed, rebuild and re-run.");

            return;
        }

        // The coordinator's resolution wins over the class attribute discovery just read. It is the
        // side that knows where instances come from, and a lifetime decided twice is a lifetime the
        // two processes can disagree about - which would make an isolated and an in-process number
        // differ for a reason that has nothing to do with the process boundary.
        if (request.InstanceLifetimeOverride is { } lifetime)
            suite = suite with { Lifetime = lifetime };

        var requested = new HashSet<string>(request.BenchmarkNames, StringComparer.Ordinal);

        var selected = requested.Count == 0
            ? suite.Benchmarks
            : suite.Benchmarks.Where(b => requested.Contains(b.DisplayName)).ToList();

        if (selected.Count == 0)
        {
            Fault($"None of the requested benchmarks were found on '{request.DeclaringTypeFullName}'.");

            return;
        }

        var outcome = await DiscoveredGroupExecutor.RunAsync(
                suite,
                selected,
                options,
                instanceFactory,
                request.Order,
                request.Seed,
                request.StartIndex,
                request.TotalBenchmarks,
                progress,
                progress.AsObserver(),
                postSuiteCleanup: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (outcome.InstantiationFailed)
        {
            Fault(
                $"'{request.DeclaringTypeFullName}' could not be instantiated in the worker."
                + (outcome.Failure is { Length: > 0 } why ? $" {why}" : ""));

            return;
        }

        // Results were sent incrementally as each benchmark completed (above, via
        // progress.OnBenchmarkCompleted), so there is nothing to batch here.
    }

    /// <summary>
    ///     Builds the suite by invoking the user's own factory here, then measures it.
    ///     <para>
    ///         Nothing about the suite travelled over the wire - only the address of the factory
    ///         did. Everything the previous design listed as unable to cross a process boundary
    ///         (custom outlier detectors, significance tests, observers, instance factories, setup
    ///         and teardown delegates) is a live object here, because the user's own code
    ///         constructed it in this process.
    ///     </para>
    ///     <para>
    ///         The measurement options come from the suite the factory built, not from
    ///         <see cref="RunGroupPayload.Options" />. The request's copy exists so the coordinator
    ///         could pick the right runtime profile to launch this process under; deserializing it
    ///         back over the factory's own configuration would discard exactly the parts that
    ///         cannot be serialized.
    ///     </para>
    /// </summary>
    private async Task RunPlanAsync(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        ResolvedReceivers receivers,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        if (request.Plan is not { } plan)
        {
            Fault("A plan group carries no benchmark plan address.");
            return;
        }

        // Both addressing modes, the invocation, the null check and the return-type check are one
        // call: a plan is a recipe like any other, and the only thing particular to it is that what
        // it produces is a BenchmarkSuite.
        if (!FactoryResolver.TryInvoke<BenchmarkSuite>(
                context, request.TargetAssemblyPath, plan, receivers, out var suite, out var error, out var detail))
        {
            Fault(Capitalize(error!), detail);
            return;
        }

        // The suite measures itself and forwards each result as it completes (above, via
        // progress.OnBenchmarkCompleted), so the returned outcome is not needed here - nothing is
        // batched to group end.
        await suite
            .MeasureInWorkerAsync(
                progress,
                progress.AsObserver(),
                request.Seed,
                request.StartIndex,
                request.TotalBenchmarks,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunLambdasAsync(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        MeasurementOptions options,
        ResolvedReceivers receivers,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        var index = 0;

        // The suite's own setup runs here, once, before any body is measured - which is the whole
        // reason it travels rather than running in the coordinator, where it would prepare state in a
        // process that goes on to measure nothing.
        if (!TryRunSuiteHook(context, request.SuiteSetup, receivers, "setup"))
            return;

        try
        {
            // Shuffled here, in the measuring process, for the same reason the discovered-class path
            // orders inside the worker: the coordinator sends a set of addresses, and the order they are
            // measured in is a property of the measurement rather than of the request.
            foreach (var body in RunOrdering.Apply(request.Bodies, request.Order, request.Seed))
            {
                cancellationToken.ThrowIfCancellationRequested();

                await progress
                    .OnBenchmarkStarting(body.DisplayName, request.StartIndex + index + 1, request.TotalBenchmarks)
                    .ConfigureAwait(false);

                if (!BodyResolver.TryResolve(
                        context, body, receivers, out var resolved, out var boundArguments, out var error))
                {
                    Fault(
                        $"'{body.DisplayName}' could not be measured because {error}",
                        benchmarkName: body.DisplayName);

                    index++;
                    continue;
                }

                if (!TryResolveSampleHooks(context, body, receivers, boundArguments, out var sampleSetup,
                        out var sampleTeardown, out var hookError))
                {
                    // Reported as this benchmark's own failure rather than measured without its hooks.
                    // A body measured with its setup silently dropped produces a plausible number for
                    // work that never happened, which is the failure this whole area exists to refuse.
                    Fault(
                        $"'{body.DisplayName}' could not be measured because {hookError}",
                        benchmarkName: body.DisplayName);

                    index++;
                    continue;
                }

                var spec = new RunSpec
                {
                    Options = options,
                    Progress = progress,
                    Observer = progress.AsObserver(),
                    SampleSetup = sampleSetup,
                    SampleTeardown = sampleTeardown,
                };

                var outcome = await DelegateDispatch
                    .MeasureAsync(body.DisplayName, resolved, spec, cancellationToken)
                    .ConfigureAwait(false);

                // The lambda path does not flow through SuiteRunner, so OnBenchmarkCompleted is not
                // raised for it automatically the way it is for the discovered, plan, and test-method
                // paths. Sending here - one result, as soon as it is measured - is the same contract:
                // the result is on the wire before the next body starts, so a crash on a later body can
                // no longer lose this one.
                await progress.OnBenchmarkCompleted(outcome.Result).ConfigureAwait(false);

                index++;
            }
        }
        finally
        {
            // In a finally so a cancelled or faulted group still releases whatever the setup acquired.
            // Its own failure is reported but cannot mask the group's results, which are already sent.
            TryRunSuiteHook(context, request.SuiteTeardown, receivers, "teardown");
        }
    }

    /// <summary>
    ///     Resolves and invokes one suite-level lifecycle delegate. Returns <c>false</c> when it could
    ///     not be resolved or threw, having already reported the fault.
    /// </summary>
    private bool TryRunSuiteHook(
        BenchmarkLoadContext context,
        BodyRef? hook,
        ResolvedReceivers receivers,
        string role)
    {
        if (hook is null)
            return true;

        if (!BodyResolver.TryResolve(context, hook, receivers, out var resolved, out var error))
        {
            Fault($"The suite's {role} could not be resolved because {error}");

            return false;
        }

        if (resolved is not Action action)
        {
            Fault($"The suite's {role} resolved to {resolved.GetType().Name} rather than an Action.");

            return false;
        }

        try
        {
            action();

            return true;
        }
        catch (Exception ex)
        {
            Fault($"The suite's {role} threw: {ex.Message}", ex.ToString());

            return false;
        }
    }

    /// <summary>
    ///     Resolves a body's per-iteration hooks, which the engine invokes outside the timed region.
    /// </summary>
    /// <param name="boundArguments">
    ///     The values the body's parameters were filled with, so a hook that takes them acts on the
    ///     same prepared state the body reads rather than on a second copy of it.
    /// </param>
    private static bool TryResolveSampleHooks(
        BenchmarkLoadContext context,
        BodyRef body,
        ResolvedReceivers receivers,
        IReadOnlyList<object?> boundArguments,
        out Action? setup,
        out Action? teardown,
        out string? error)
    {
        setup = null;
        teardown = null;
        error = null;

        if (!TryResolveHook(
                context, body.SampleSetup, receivers, boundArguments, "per-iteration setup", out setup, out error))
        {
            return false;
        }

        return TryResolveHook(
            context, body.SampleTeardown, receivers, boundArguments, "per-iteration teardown", out teardown,
            out error);
    }

    private static bool TryResolveHook(
        BenchmarkLoadContext context,
        BodyRef? hook,
        ResolvedReceivers receivers,
        IReadOnlyList<object?> boundArguments,
        string role,
        out Action? action,
        out string? error)
    {
        action = null;
        error = null;

        if (hook is null)
            return true;

        if (!BodyResolver.TryResolveHook(context, hook, receivers, boundArguments, out var resolved, out var resolveError))
        {
            error = $"its {role} could not be resolved: {resolveError}";

            return false;
        }

        action = resolved;

        return true;
    }

    /// <summary>
    ///     Rebuilds the two strategy objects that could not travel as values. They arrive as
    ///     assembly-qualified type names and are constructed here through the worker's load
    ///     context, so a user's custom detector or significance test is the same class the
    ///     coordinator would have used - not a silent fallback to the built-in one.
    /// </summary>
    /// <param name="substitutions">
    ///     Collects a sentence per strategy that could not be rebuilt here. Each one is attached to
    ///     every result in the group, because the alternative - a line on stderr - is only ever read
    ///     when the worker dies, and a group that completes normally is exactly the case where the
    ///     substitution goes unnoticed.
    /// </param>
    private MeasurementOptions ResolveStrategies(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        ResolvedReceivers receivers,
        List<string> substitutions)
    {
        var options = request.Options;

        // The factory is run once here and its product pinned, rather than re-run per resolution: the
        // caller's factory is theirs to have side effects in, and the coordinator invokes it once too.
        if (RunStrategyFactory<IOutlierDetector>(
                context, request, request.OutlierDetectorFactory, receivers, substitutions) is
            { } detector)
        {
            options = options with { OutlierDetector = () => detector };
        }

        if (RunStrategyFactory<ISignificanceTest>(
                context, request, request.SignificanceTestFactory, receivers, substitutions) is
            { } test)
        {
            options = options with { SignificanceTest = () => test };
        }

        return options;
    }

    /// <summary>
    ///     Resolves and invokes an addressed factory, returning what it produced.
    /// </summary>
    /// <remarks>
    ///     A failure here records a substitution and returns <c>null</c>, letting the caller fall through
    ///     to the type name and then to the built-in strategy. Not a fault: the benchmark bodies are still
    ///     measurable, and losing a custom scoring method is worth saying rather than a dead group - but
    ///     it has to be said <i>on the results</i>, because the alternative is a row that reports itself
    ///     as isolated and unremarkable while having been scored under a method nobody chose.
    /// </remarks>
    private static T? RunStrategyFactory<T>(
        BenchmarkLoadContext context,
        RunGroupPayload request,
        AddressedFactory? factory,
        ResolvedReceivers receivers,
        List<string> substitutions)
        where T : class
    {
        if (factory is null)
            return null;

        if (FactoryResolver.TryInvoke<T>(
                context, request.TargetAssemblyPath, factory, receivers, out var produced, out var error, out _))
        {
            return produced;
        }

        Substituted<T>(substitutions, error ?? "it could not be built in the measuring process.");

        return null;
    }

    /// <summary>
    ///     Records that a requested strategy was replaced by the built-in one, on stderr for a live
    ///     reader and on every result for everybody else.
    /// </summary>
    private static void Substituted<T>(List<string> substitutions, string reason)
    {
        var warning = $"The {typeof(T).Name} requested for this run could not be rebuilt in the "
                      + $"measurement worker, so these results were scored with the built-in "
                      + $"{typeof(T).Name} instead: {reason}";

        substitutions.Add(warning);

        Console.Error.WriteLine($"nbworker: {warning}");
    }

    /// <summary>
    ///     Upper-cases the first character of a resolver message, which is phrased to sit mid-sentence
    ///     after a role, so it can start a fault of its own.
    /// </summary>
    private static string Capitalize(string message)
        => message.Length == 0 || char.IsUpper(message[0])
            ? message
            : char.ToUpperInvariant(message[0]) + message[1..];

    /// <param name="context">
    ///     The <b>group's</b> load context. This used to build one of its own, in direct contradiction
    ///     of the rule stated at the top of <see cref="RunGroupAsync" /> - a second context loads a
    ///     second copy of the target assembly, so the same logical type from the two is two distinct
    ///     Type identities, with its static constructor run twice. It survived only because
    ///     <see cref="IOutlierDetector" /> and <see cref="ISignificanceTest" /> come from NBenchmark,
    ///     which both contexts unify to Default; the moment a strategy touched a user type it was in
    ///     the failure that comment describes. Both callers had the group's context in scope already.
    /// </param>
    private static T? Construct<T>(string typeName, BenchmarkLoadContext context, List<string> substitutions)
        where T : class
    {
        // TypeNames tries the default context first - a strategy defined by the engine is there, and
        // the common case stays free of load-context subtleties - then the target's own graph.
        var type = TypeNames.Resolve(typeName, context);

        if (type is null)
        {
            Substituted<T>(substitutions, $"the type '{typeName}' could not be loaded here.");

            return null;
        }

        try
        {
            if (Activator.CreateInstance(type) as T is { } constructed)
                return constructed;

            // `as` returning null is the quietest failure in this method and used to be the only one
            // with nothing to say: the object was built and is not a T, which happens when the
            // interface itself resolved from two different load contexts.
            Substituted<T>(
                substitutions,
                $"'{typeName}' was constructed but is not a {typeof(T).Name} this process recognises, "
                + "which usually means NBenchmark was loaded twice.");

            return null;
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
        {
            Substituted<T>(
                substitutions,
                $"'{typeName}' has no usable parameterless constructor ({ex.Message}).");

            return null;
        }
    }

    /// <summary>
    ///     Runs the calibration standard in this process, so a gate can divide by a number measured
    ///     under the same runtime configuration as the benchmark it is judging.
    /// </summary>
    private static CalibrationPayload? MeasureCalibration()
    {
        try
        {
            var calibration = CalibrationStandard.Measure();

            return new CalibrationPayload
            {
                MeanNs = calibration.MeanNs,
                MedianNs = calibration.MedianNs,
                Samples = calibration.Samples,
            };
        }
        catch (Exception ex)
        {
            // Never fatal. A group that measured its benchmark successfully must not be lost over
            // the divisor; the coordinator falls back to its own calibration and says so.
            Console.Error.WriteLine($"nbworker: calibration failed ({ex.Message}); the host will use its own.");

            return null;
        }
    }

    /// <summary>
    ///     Routes a frame through the group's queue when one is open, and straight down the pipe
    ///     otherwise - the handshake and any pre-group protocol fault have no queue behind them.
    /// </summary>
    private void Enqueue(WorkerFrame frame)
    {
        if (_queue is { } queue)
            queue.Enqueue(frame);
        else
            _channel.WriteAsync(frame, CancellationToken.None).GetAwaiter().GetResult();
    }

    private void Fault(string message, string? detail = null, string? benchmarkName = null)
        => Enqueue(WorkerFrame.Of(new FaultPayload
        {
            Message = message,
            Detail = detail,
            BenchmarkName = benchmarkName,
        }));

    /// <summary>
    ///     The shared frameworks this process was started with, for a diagnostic about an assembly
    ///     that could not be found.
    /// </summary>
    /// <remarks>
    ///     Read from the host's own record of which dependency manifests it merged into the
    ///     trusted-platform-assembly list. A framework's manifest lives at
    ///     <c>&lt;root&gt;/shared/&lt;framework&gt;/&lt;version&gt;/&lt;framework&gt;.deps.json</c>, so
    ///     the shape of the path is what distinguishes it from the worker's own manifest sitting
    ///     beside the worker.
    /// </remarks>
    private static IReadOnlyList<string> LoadedSharedFrameworks()
    {
        if (AppContext.GetData("APP_CONTEXT_DEPS_FILES") is not string depsFiles)
            return ["unknown"];

        var frameworks = new List<string>(2);

        foreach (var deps in depsFiles.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Path.GetDirectoryName(deps) is not { } versionDirectory
                || Path.GetDirectoryName(versionDirectory) is not { } frameworkDirectory
                || !string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(frameworkDirectory)),
                    "shared",
                    StringComparison.OrdinalIgnoreCase))
                continue;

            frameworks.Add($"{Path.GetFileName(frameworkDirectory)} {Path.GetFileName(versionDirectory)}");
        }

        return frameworks.Count == 0 ? ["unknown"] : frameworks;
    }
}
