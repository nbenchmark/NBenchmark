using System.Reflection;
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
    ///     Runs until end-of-stream or a shutdown frame. Returns the process exit code.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
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

        while (true)
        {
            var frame = await _channel.ReadAsync(cancellationToken).ConfigureAwait(false);

            // End of stream. The coordinator closed its write end - deliberately, or because it
            // died. Either way this worker has nothing left to serve and no reason to linger.
            if (frame is null)
                return WorkerExitCode.Success;

            switch (frame.Kind)
            {
                case WorkerFrameKind.Shutdown:
                    return WorkerExitCode.Success;

                case WorkerFrameKind.RunGroup when frame.RunGroup is not null:
                    await RunGroupAsync(frame.RunGroup, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    Fault($"Unexpected frame {frame.Kind} while idle.");
                    break;
            }
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
        try
        {
            var options = ResolveStrategies(request);

            // The worker was launched with the runtime profile already applied to its environment
            // block - that is the only moment it could have been. Affinity and priority, by
            // contrast, are settable at any time and belong here.
            using var _ = EnvironmentControl.Apply(options.Environment);

            _queue = new FrameQueue(_channel, cancellationToken);
            var progress = new StreamingProgress(_queue, cancellationToken);

            switch (request.Kind)
            {
                case WorkGroupKind.DiscoveredClass:
                    await RunDiscoveredClassAsync(request, options, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkGroupKind.Lambdas:
                    await RunLambdasAsync(request, options, progress, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    Fault($"Group kind {request.Kind} is not supported by this worker.");
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fault($"The group failed: {ex.Message}", ex.ToString());
        }
        finally
        {
            // Drain before the terminal frame, so the coordinator never sees a group complete
            // while that group's own events are still in flight behind it.
            if (_queue is not null)
                await _queue.DrainAsync().ConfigureAwait(false);

            await _channel
                .WriteAsync(
                    WorkerFrame.Of(new GroupCompletedPayload { GroupId = request.GroupId }), cancellationToken)
                .ConfigureAwait(false);

            _queue = null;
        }
    }

    private async Task RunDiscoveredClassAsync(
        RunGroupPayload request,
        MeasurementOptions options,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        var context = new BenchmarkLoadContext(request.TargetAssemblyPath);
        var target = context.LoadFromAssemblyPath(Path.GetFullPath(request.TargetAssemblyPath));

        var discoverer = new BenchmarkDiscoverer(request.DefaultInstanceLifetime);
        var suites = discoverer.Discover(target);

        var suite = suites.FirstOrDefault(s => s.Type.FullName == request.DeclaringTypeFullName);

        if (suite is null)
        {
            Fault(
                $"Class '{request.DeclaringTypeFullName}' was not found in "
                + $"'{Path.GetFileName(request.TargetAssemblyPath)}'. If the class was renamed or "
                + "removed, rebuild and re-run.");

            return;
        }

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
                // A worker has no instance factory. Anything requiring one is not routed here -
                // the coordinator keeps it in-process and labels it, rather than substituting a
                // parameterless constructor and reporting the result as if nothing changed.
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
            Fault($"'{request.DeclaringTypeFullName}' could not be instantiated in the worker.");

            return;
        }

        SendResults(outcome.Results, r => outcome.RawSamples.GetValueOrDefault(r.Name, []));
    }

    private async Task RunLambdasAsync(
        RunGroupPayload request,
        MeasurementOptions options,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        var context = new BenchmarkLoadContext(request.TargetAssemblyPath);
        var index = 0;

        foreach (var body in request.Bodies)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var spec = new RunSpec
            {
                Options = options,
                Progress = progress,
                Observer = progress.AsObserver(),
            };

            await progress
                .OnBenchmarkStarting(body.DisplayName, request.StartIndex + index + 1, request.TotalBenchmarks)
                .ConfigureAwait(false);

            if (!BodyResolver.TryResolve(context, body, out var resolved, out var error))
            {
                Fault(
                    $"'{body.DisplayName}' could not be measured because {error}",
                    benchmarkName: body.DisplayName);

                index++;
                continue;
            }

            var outcome = await BodyResolver
                .MeasureAsync(body.DisplayName, resolved, spec, cancellationToken)
                .ConfigureAwait(false);

            SendResults([outcome.Result], _ => outcome.RawSamples);

            index++;
        }
    }

    /// <summary>
    ///     Rebuilds the two strategy objects that could not travel as values. They arrive as
    ///     assembly-qualified type names and are constructed here through the worker's load
    ///     context, so a user's custom detector or significance test is the same class the
    ///     coordinator would have used - not a silent fallback to the built-in one.
    /// </summary>
    private MeasurementOptions ResolveStrategies(RunGroupPayload request)
    {
        var options = request.Options;

        if (request.OutlierDetectorTypeName is { Length: > 0 } detectorName)
        {
            options = options with
            {
                OutlierDetector = Construct<IOutlierDetector>(detectorName, request.TargetAssemblyPath),
            };
        }

        if (request.SignificanceTestTypeName is { Length: > 0 } testName)
        {
            options = options with
            {
                SignificanceTest = Construct<ISignificanceTest>(testName, request.TargetAssemblyPath),
            };
        }

        return options;
    }

    private static T? Construct<T>(string typeName, string targetAssemblyPath) where T : class
    {
        // Types defined by the engine resolve from the default context; a user's own strategy
        // resolves from the target's graph. Trying the plain lookup first keeps the common case
        // free of load-context subtleties.
        var type = Type.GetType(typeName, throwOnError: false);

        if (type is null)
        {
            var context = new BenchmarkLoadContext(targetAssemblyPath);

            type = Type.GetType(
                typeName,
                name => context.LoadFromAssemblyName(name),
                (_, name, ignoreCase) => Type.GetType(name, throwOnError: false, ignoreCase),
                throwOnError: false);
        }

        if (type is null)
        {
            // Not fatal: the engine falls back to its built-in strategy. Saying so on stderr is
            // better than silently measuring under a different statistical method than requested.
            Console.Error.WriteLine(
                $"nbworker: could not load '{typeName}'; using the built-in {typeof(T).Name} instead.");

            return null;
        }

        try
        {
            return Activator.CreateInstance(type) as T;
        }
        catch (Exception ex) when (ex is MissingMethodException or MemberAccessException or TargetInvocationException)
        {
            Console.Error.WriteLine(
                $"nbworker: '{typeName}' has no usable parameterless constructor ({ex.Message}); "
                + $"using the built-in {typeof(T).Name} instead.");

            return null;
        }
    }

    /// <summary>
    ///     Ships each result with its own samples.
    ///     <para>
    ///         Names are left exactly as the engine produced them. A discovered benchmark is already
    ///         named <c>Class.Method</c> by its envelope, so applying
    ///         <see cref="RunGroupPayload.DisplayPrefix" /> here would double the class name. The
    ///         prefix exists for naming benchmarks that produced <i>no</i> result, which is the
    ///         coordinator's job - see <see cref="WorkerGroupRunner.ToErroredResults" />.
    ///     </para>
    /// </summary>
    private void SendResults(
        IReadOnlyList<BenchmarkResult> results,
        Func<BenchmarkResult, double[]> resolveSamples)
    {
        foreach (var result in results)
        {
            Enqueue(WorkerFrame.Of(new BenchmarkCompletedPayload
            {
                // RawSamples travel in the sibling property, so the copy inside the result is
                // cleared rather than duplicated on the wire.
                Result = result with { RawSamples = [] },
                RawSamples = resolveSamples(result) ?? [],
            }));
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
}

/// <summary>Exit codes the coordinator can distinguish when a worker dies early.</summary>
internal static class WorkerExitCode
{
    public const int Success = 0;
    public const int BadArguments = 64;
    public const int NoHandshake = 65;
    public const int ProtocolError = 66;
    public const int Crashed = 70;
}
