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
    ///     The sample budget and seed for the group in flight, held here because every path that
    ///     produces results funnels through <see cref="SendResults" /> and none of them carry the
    ///     request that far.
    /// </summary>
    private int _maxRawSamples = MeasurementOptions.DefaultMaxRawSamples;

    private int _sampleSeed;

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
            {
                // Said out loud because from the coordinator's side this is indistinguishable from
                // a crash: its next read returns end-of-stream either way. Without this line a
                // worker that exited for a perfectly ordinary reason looks like a lost process.
                Console.Error.WriteLine("nbworker: inbound stream closed while idle; exiting.");

                return WorkerExitCode.Success;
            }

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
        // Held outside the try so the terminal flush can reach it on the failure path too: a group
        // that faulted mid-measurement has already streamed samples, and leaving them in the buffer
        // would drop the events leading up to whatever went wrong - the ones worth having.
        StreamingProgress? progress = null;

        try
        {
            // One load context for the whole group. Not an optimisation: a second context loads a
            // second copy of the target assembly, and the same logical type from two contexts is two
            // distinct Type identities. That broke the service-provider factory outright - the worker
            // built a container registering the type from one context and then asked it for the type
            // from the other, which is simply not registered. Anything that resolves user code from a
            // request has to do it through the same context that discovery used.
            var context = new BenchmarkLoadContext(request.TargetAssemblyPath);

            var options = ResolveStrategies(request, context);

            _maxRawSamples = options.MaxRawSamples;

            // The run's own seed when it has one, so the same seed ships the same subset. Groups
            // without a seed still need a fixed value rather than a time-derived one, or two runs of
            // an identical configuration would disagree about which samples they sent.
            _sampleSeed = request.Seed ?? 0;

            // The worker was launched with the runtime profile already applied to its environment
            // block - that is the only moment it could have been. Affinity and priority, by
            // contrast, are settable at any time and belong here.
            using var _ = EnvironmentControl.Apply(options.Environment);

            _queue = new FrameQueue(_channel, cancellationToken);
            progress = new StreamingProgress(_queue, cancellationToken, options.StreamSamples);

            switch (request.Kind)
            {
                case WorkGroupKind.DiscoveredClass:
                    await RunDiscoveredClassAsync(request, context, options, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkGroupKind.Lambdas:
                    await RunLambdasAsync(request, context, options, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkGroupKind.Plan:
                    await RunPlanAsync(request, context, progress, cancellationToken).ConfigureAwait(false);
                    break;

                case WorkGroupKind.TestMethod:
                    await RunTestMethodAsync(request, context, options, progress, cancellationToken).ConfigureAwait(false);
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
            // Into the queue first, then drain it: a batch still in the sample buffer has not been
            // enqueued at all, so draining without flushing would lose it rather than wait for it.
            progress?.FlushSamples();

            // Drain before the terminal frame, so the coordinator never sees a group complete
            // while that group's own events are still in flight behind it.
            if (_queue is not null)
                await _queue.DrainAsync().ConfigureAwait(false);

            // Measured here, after the group's work, so it reflects the process the benchmark
            // actually ran in rather than a freshly-started one. Its whole purpose is to be
            // divisible into that benchmark's number.
            var calibration = request.MeasureCalibration ? MeasureCalibration() : null;

            await _channel
                .WriteAsync(
                    WorkerFrame.Of(new GroupCompletedPayload
                    {
                        GroupId = request.GroupId,
                        Calibration = calibration,
                    }),
                    cancellationToken)
                .ConfigureAwait(false);

            _queue = null;
        }
    }

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
            if (!TryResolveTestMethod(module, requested, out var method, out var arguments))
                return;

            resolvedMethods.Add((method!, arguments!, requested.DisplayName));
        }

        var suite = BenchmarkDiscoverer.DefineExplicit(resolvedMethods);

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
                + "though it appeared to have a usable parameterless constructor.");

            return;
        }

        // Discovery names a result '<Class>.<DisplayName>', which is the right convention for a
        // benchmark class and the wrong one here - the caller's display name is already qualified
        // however its test framework qualifies names. Renamed on this side, where the convention that
        // produced the name is known, rather than reconstructed by the coordinator from a rule it
        // would then own a second copy of.
        var renamed = new List<BenchmarkResult>(outcome.Results.Count);
        var samplesByName = new Dictionary<string, double[]>(StringComparer.Ordinal);

        foreach (var result in outcome.Results)
        {
            var samples = outcome.RawSamples.GetValueOrDefault(result.Name, []);

            var requestedName = resolvedMethods
                .Select(m => m.DisplayName)
                .FirstOrDefault(name => result.Name == $"{suite.Type.Name}.{name}");

            var final = requestedName is null ? result : result with { Name = requestedName };

            renamed.Add(final);
            samplesByName[final.Name] = samples;
        }

        SendResults(renamed, r => samplesByName.GetValueOrDefault(r.Name, []));
    }

    /// <summary>
    ///     Resolves one addressed test method and rebuilds its arguments, faulting the group when
    ///     either cannot be done.
    /// </summary>
    private bool TryResolveTestMethod(
        Module module,
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
        out Func<Type, InstanceHandle>? instanceFactory)
    {
        instanceFactory = null;

        if (request.ServiceProviderFactory is null)
            return true;

        if (!BodyResolver.TryResolve(context, request.ServiceProviderFactory, out var resolved, out var error))
        {
            Fault($"The service provider factory could not be resolved because {error}");

            return false;
        }

        IServiceProvider? provider;

        try
        {
            provider = resolved.DynamicInvoke() as IServiceProvider;
        }
        catch (Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;

            Fault(
                $"The service provider factory threw {inner.GetType().Name}: {inner.Message}",
                inner.ToString());

            return false;
        }

        if (provider is null)
        {
            Fault("The service provider factory returned null, or something that is not an IServiceProvider.");

            return false;
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

    private async Task RunDiscoveredClassAsync(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        MeasurementOptions options,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
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

        // A container built here, from the caller's own registrations, rather than a live one sent
        // across - which is impossible - or a parameterless constructor substituted for it, which would
        // measure a differently-configured object under the right name. Absent a factory this stays
        // null and the group was never routed here in the first place.
        if (!TryBuildInstanceFactory(request, context, out var instanceFactory))
            return;

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
            Fault($"'{request.DeclaringTypeFullName}' could not be instantiated in the worker.");

            return;
        }

        SendResults(outcome.Results, r => outcome.RawSamples.GetValueOrDefault(r.Name, []));
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
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        Func<BenchmarkSuite>? factory;
        string description;

        if (request.PlanMethodName is { Length: > 0 } planMethodName)
        {
            // Name resolution: the assembly is a different build of the same source, so tokens from
            // the coordinator's build mean nothing here.
            description = $"{request.DeclaringTypeFullName}.{planMethodName}";

            if (!TryResolvePlanByName(context, request, out factory, out var nameError))
            {
                Fault($"The benchmark plan '{description}' could not be resolved because {nameError}");
                return;
            }
        }
        else
        {
            if (request.Bodies.Count != 1)
            {
                Fault($"A plan group must carry exactly one factory address; got {request.Bodies.Count}.");
                return;
            }

            var planRef = request.Bodies[0];
            description = planRef.DisplayName;

            if (!BodyResolver.TryResolve(context, planRef, out var resolved, out var error))
            {
                Fault($"The benchmark plan '{description}' could not be resolved because {error}");
                return;
            }

            factory = resolved as Func<BenchmarkSuite>;

            if (factory is null)
            {
                Fault(
                    $"'{description}' resolved to {resolved.GetType().Name} rather than a "
                    + $"{nameof(Func<BenchmarkSuite>)}. A benchmark plan must be a parameterless method "
                    + $"returning {nameof(BenchmarkSuite)}.");

                return;
            }
        }

        if (factory is null)
        {
            Fault($"The benchmark plan '{description}' could not be bound to a factory.");
            return;
        }

        BenchmarkSuite suite;

        try
        {
            suite = factory();
        }
        catch (Exception ex)
        {
            // The factory is user code and can fail for any reason. Reporting it as the group's
            // fault is far more useful than letting it surface as a dead worker.
            Fault($"The benchmark plan '{description}' threw while building the suite: {ex.Message}",
                ex.ToString());

            return;
        }

        if (suite is null)
        {
            Fault($"The benchmark plan '{description}' returned null.");
            return;
        }

        var outcome = await suite
            .MeasureInWorkerAsync(
                progress,
                progress.AsObserver(),
                request.Seed,
                request.StartIndex,
                request.TotalBenchmarks,
                cancellationToken)
            .ConfigureAwait(false);

        SendResults(outcome.Results, r => outcome.RawSamples.GetValueOrDefault(r.Name, []));
    }

    /// <summary>
    ///     Binds a plan factory by fully-qualified name from the assembly under test.
    /// </summary>
    /// <remarks>
    ///     The shape checks mirror <c>BenchmarkPlanDiscovery</c>'s, because a plan that the
    ///     coordinator accepted must not be rejected here for a different reason - and because the
    ///     assembly here is a <i>different build</i>, where the method genuinely might have changed
    ///     shape under a different target framework's conditional compilation.
    /// </remarks>
    private static bool TryResolvePlanByName(
        BenchmarkLoadContext context,
        RunGroupPayload request,
        out Func<BenchmarkSuite>? factory,
        out string? error)
    {
        factory = null;
        error = null;

        var target = context.LoadFromAssemblyPath(Path.GetFullPath(request.TargetAssemblyPath));
        var type = target.GetType(request.DeclaringTypeFullName!, throwOnError: false);

        if (type is null)
        {
            error = $"the type '{request.DeclaringTypeFullName}' was not found in "
                    + $"'{Path.GetFileName(request.TargetAssemblyPath)}'.";

            return false;
        }

        var method = type.GetMethod(
            request.PlanMethodName!,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);

        if (method is null)
        {
            error = $"'{request.DeclaringTypeFullName}' has no static parameterless method named "
                    + $"'{request.PlanMethodName}'.";

            return false;
        }

        if (!typeof(BenchmarkSuite).IsAssignableFrom(method.ReturnType))
        {
            error = $"'{request.PlanMethodName}' returns {method.ReturnType.Name} rather than "
                    + $"{nameof(BenchmarkSuite)}.";

            return false;
        }

        factory = method.CreateDelegate<Func<BenchmarkSuite>>();

        return true;
    }

    private async Task RunLambdasAsync(
        RunGroupPayload request,
        BenchmarkLoadContext context,
        MeasurementOptions options,
        StreamingProgress progress,
        CancellationToken cancellationToken)
    {
        var index = 0;

        // The suite's own setup runs here, once, before any body is measured - which is the whole
        // reason it travels rather than running in the coordinator, where it would prepare state in a
        // process that goes on to measure nothing.
        if (!TryRunSuiteHook(context, request.SuiteSetup, "setup"))
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

                if (!BodyResolver.TryResolve(context, body, out var resolved, out var error))
                {
                    Fault(
                        $"'{body.DisplayName}' could not be measured because {error}",
                        benchmarkName: body.DisplayName);

                    index++;
                    continue;
                }

                if (!TryResolveIterationHooks(context, body, out var iterationSetup, out var iterationTeardown,
                        out var hookError))
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
                    IterationSetup = iterationSetup,
                    IterationTeardown = iterationTeardown,
                };

                var outcome = await DelegateDispatch
                    .MeasureAsync(body.DisplayName, resolved, spec, cancellationToken)
                    .ConfigureAwait(false);

                SendResults([outcome.Result], _ => outcome.RawSamples);

                index++;
            }
        }
        finally
        {
            // In a finally so a cancelled or faulted group still releases whatever the setup acquired.
            // Its own failure is reported but cannot mask the group's results, which are already sent.
            TryRunSuiteHook(context, request.SuiteTeardown, "teardown");
        }
    }

    /// <summary>
    ///     Resolves and invokes one suite-level lifecycle delegate. Returns <c>false</c> when it could
    ///     not be resolved or threw, having already reported the fault.
    /// </summary>
    private bool TryRunSuiteHook(BenchmarkLoadContext context, BodyRef? hook, string role)
    {
        if (hook is null)
            return true;

        if (!BodyResolver.TryResolve(context, hook, out var resolved, out var error))
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
    private static bool TryResolveIterationHooks(
        BenchmarkLoadContext context,
        BodyRef body,
        out Action? setup,
        out Action? teardown,
        out string? error)
    {
        setup = null;
        teardown = null;
        error = null;

        if (!TryResolveHook(context, body.IterationSetup, "per-iteration setup", out setup, out error))
            return false;

        return TryResolveHook(context, body.IterationTeardown, "per-iteration teardown", out teardown, out error);
    }

    private static bool TryResolveHook(
        BenchmarkLoadContext context,
        BodyRef? hook,
        string role,
        out Action? action,
        out string? error)
    {
        action = null;
        error = null;

        if (hook is null)
            return true;

        if (!BodyResolver.TryResolve(context, hook, out var resolved, out var resolveError))
        {
            error = $"its {role} could not be resolved: {resolveError}";

            return false;
        }

        if (resolved is not Action typed)
        {
            error = $"its {role} resolved to {resolved.GetType().Name} rather than an Action.";

            return false;
        }

        action = typed;

        return true;
    }

    /// <summary>
    ///     Rebuilds the two strategy objects that could not travel as values. They arrive as
    ///     assembly-qualified type names and are constructed here through the worker's load
    ///     context, so a user's custom detector or significance test is the same class the
    ///     coordinator would have used - not a silent fallback to the built-in one.
    /// </summary>
    private MeasurementOptions ResolveStrategies(RunGroupPayload request, BenchmarkLoadContext context)
    {
        var options = request.Options;

        // A factory wins over a type name. It is the stronger mechanism - it reproduces the caller's own
        // object with its own constructor arguments, where a type name can only reach a parameterless
        // constructor - so where both are present the type name is the weaker fallback, not a conflict.
        if (RunFactory<IOutlierDetector>(context, request.OutlierDetectorFactory, "outlier detector") is
            { } detector)
        {
            options = options with { OutlierDetector = detector };
        }
        else if (request.OutlierDetectorTypeName is { Length: > 0 } detectorName)
        {
            options = options with
            {
                OutlierDetector = Construct<IOutlierDetector>(detectorName, request.TargetAssemblyPath),
            };
        }

        if (RunFactory<ISignificanceTest>(context, request.SignificanceTestFactory, "significance test") is
            { } test)
        {
            options = options with { SignificanceTest = test };
        }
        else if (request.SignificanceTestTypeName is { Length: > 0 } testName)
        {
            options = options with
            {
                SignificanceTest = Construct<ISignificanceTest>(testName, request.TargetAssemblyPath),
            };
        }

        return options;
    }

    /// <summary>
    ///     Resolves and invokes an addressed factory, returning what it produced.
    /// </summary>
    /// <remarks>
    ///     A failure here is reported on stderr and returns <c>null</c>, letting the caller fall through
    ///     to the type name and then to the built-in strategy. Not a fault: the benchmark bodies are still
    ///     measurable, and losing a custom scoring method is worth a loud line rather than a dead group -
    ///     but it must be loud, because the alternative is a result scored under a method nobody chose.
    /// </remarks>
    private static T? RunFactory<T>(BenchmarkLoadContext context, BodyRef? factory, string role) where T : class
    {
        if (factory is null)
            return null;

        if (!BodyResolver.TryResolve(context, factory, out var resolved, out var error))
        {
            Console.Error.WriteLine(
                $"nbworker: the {role} factory could not be resolved ({error}); "
                + $"using the built-in {typeof(T).Name} instead.");

            return null;
        }

        try
        {
            return resolved.DynamicInvoke() as T;
        }
        catch (Exception ex)
        {
            var inner = (ex as TargetInvocationException)?.InnerException ?? ex;

            Console.Error.WriteLine(
                $"nbworker: the {role} factory threw {inner.GetType().Name} ({inner.Message}); "
                + $"using the built-in {typeof(T).Name} instead.");

            return null;
        }
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
        // Varied per result so two benchmarks in one group do not keep the same sample positions.
        // Identical positions would not bias any single benchmark, but it would make a shared
        // periodic artifact - a GC cadence, a timer - land identically in both and look like a
        // property of the code rather than of the selection.
        var index = 0;

        foreach (var result in results)
        {
            var (samples, trimmed) = SampleReservoir.Reduce(
                resolveSamples(result) ?? [],
                result.TrimmedOrdinals,
                _maxRawSamples,
                unchecked(_sampleSeed + index++));

            Enqueue(WorkerFrame.Of(new BenchmarkCompletedPayload
            {
                // RawSamples travel in the sibling property, so the copy inside the result is
                // cleared rather than duplicated on the wire. The ordinals go with the result
                // because that is where they are declared, and they must be the remapped ones -
                // the originals index into an array that is no longer what is being sent.
                Result = result with { RawSamples = [], TrimmedOrdinals = trimmed },
                RawSamples = samples,
            }));
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
                Mean = calibration.Mean,
                Median = calibration.Median,
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
