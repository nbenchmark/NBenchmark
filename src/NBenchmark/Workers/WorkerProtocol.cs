using System.Text.Json.Serialization;

namespace NBenchmark.Workers;

/// <summary>
///     The frame vocabulary spoken between the coordinator (the process the developer runs) and
///     a worker (<c>nbworker</c>, which does the measuring).
///     <para>
///         Every frame is a <see cref="WorkerFrame" /> envelope carrying exactly one payload,
///         selected by <see cref="WorkerFrame.Kind" />. A single envelope type with nullable
///         payload slots is deliberate: it keeps the wire format trivially serializable without
///         polymorphic type discriminators, and an unknown <see cref="WorkerFrameKind" /> from a
///         mismatched build deserializes cleanly to a frame the receiver can reject with a
///         diagnostic instead of throwing inside the deserializer.
///     </para>
/// </summary>
internal enum WorkerFrameKind
{
    /// <summary>Coordinator -&gt; worker, first frame. Carries the protocol version and parent pid.</summary>
    Handshake = 0,

    /// <summary>
    ///     Worker -&gt; coordinator, in reply to <see cref="Handshake" />. The worker reports the
    ///     runtime configuration it actually started under, which is the whole reason it exists.
    /// </summary>
    Ready = 1,

    /// <summary>Coordinator -&gt; worker. Measure this comparison group and stream the results back.</summary>
    RunGroup = 2,

    /// <summary>Worker -&gt; coordinator. An <see cref="IBenchmarkProgress" /> lifecycle callback.</summary>
    Progress = 3,

    /// <summary>Worker -&gt; coordinator. An <see cref="IMeasurementObserver" /> phase event.</summary>
    ObserverPhase = 4,

    /// <summary>Worker -&gt; coordinator. One finished benchmark, with its raw samples alongside it.</summary>
    BenchmarkCompleted = 5,

    /// <summary>Worker -&gt; coordinator. The group is finished; the worker is idle and disposable.</summary>
    GroupCompleted = 6,

    /// <summary>
    ///     Worker -&gt; coordinator. Something went wrong that is not attributable to a single
    ///     benchmark body (a target that would not load, a body that could not be addressed).
    /// </summary>
    Fault = 7,

    /// <summary>Coordinator -&gt; worker. Exit cleanly.</summary>
    Shutdown = 8,

    /// <summary>
    ///     Worker -&gt; coordinator. A coalesced batch of <see cref="SampleEvent" />s. Batched
    ///     because this is the only high-volume frame in the protocol; see
    ///     <see cref="ObserverSamplesPayload" />.
    /// </summary>
    ObserverSamples = 9,

    /// <summary>
    ///     Worker -&gt; coordinator. One <see cref="IMeasurementObserver" /> detector snapshot.
    ///     Unbatched, because a benchmark emits a handful of these against thousands of samples.
    /// </summary>
    ObserverDetector = 10,
}

/// <summary>
///     Wire protocol constants shared by the coordinator and the worker.
/// </summary>
internal static class WorkerProtocol
{
    /// <summary>
    ///     Bumped on any breaking change to the frame set or payload shape. A worker and
    ///     coordinator that disagree refuse to proceed rather than misinterpreting each other -
    ///     the worker ships in the same package as the coordinator, so a mismatch means a stale
    ///     copy on disk, which is worth a loud failure.
    /// </summary>
    public const int Version = 4;

    /// <summary>
    ///     Ceiling on a single frame, so a corrupt or hostile length prefix allocates a bounded
    ///     buffer instead of attempting a multi-gigabyte one. Generous enough for an untruncated
    ///     raw-sample payload at <see cref="MeasurementOptions.MaxIterations" />.
    /// </summary>
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    /// <summary>
    ///     The argument name the coordinator uses to hand the worker the read end of the
    ///     coordinator-to-worker pipe.
    /// </summary>
    public const string InboundHandleArgument = "--inbound-handle";

    /// <summary>
    ///     The argument name the coordinator uses to hand the worker the write end of the
    ///     worker-to-coordinator pipe.
    /// </summary>
    public const string OutboundHandleArgument = "--outbound-handle";

    /// <summary>
    ///     The coordinator's process id, passed for diagnostics only. Orphan avoidance is
    ///     structural rather than supervisory: the worker blocks reading its inbound pipe, so a
    ///     coordinator that dies closes the write end, the read returns end-of-stream, and the
    ///     worker exits on its own. Measured at 7 ms on macOS with no supervision involved.
    /// </summary>
    public const string ParentProcessIdArgument = "--parent-pid";
}

/// <summary>
///     The single envelope every frame travels in. Exactly one payload property is non-null,
///     identified by <see cref="Kind" />.
/// </summary>
internal sealed record WorkerFrame
{
    public required WorkerFrameKind Kind { get; init; }

    // Each slot is suppressed when null so a frame carries only its own payload. This is done
    // per-property rather than with a global omit-nulls policy, which would break required
    // nullable members inside BenchmarkResult - see FrameChannel.SerializerOptions.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HandshakePayload? Handshake { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReadyPayload? Ready { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RunGroupPayload? RunGroup { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProgressPayload? Progress { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObserverPhasePayload? ObserverPhase { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObserverSamplesPayload? ObserverSamples { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ObserverDetectorPayload? ObserverDetector { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BenchmarkCompletedPayload? BenchmarkCompleted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GroupCompletedPayload? GroupCompleted { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FaultPayload? Fault { get; init; }

    public static WorkerFrame Of(HandshakePayload payload)
        => new() { Kind = WorkerFrameKind.Handshake, Handshake = payload };

    public static WorkerFrame Of(ReadyPayload payload)
        => new() { Kind = WorkerFrameKind.Ready, Ready = payload };

    public static WorkerFrame Of(RunGroupPayload payload)
        => new() { Kind = WorkerFrameKind.RunGroup, RunGroup = payload };

    public static WorkerFrame Of(ProgressPayload payload)
        => new() { Kind = WorkerFrameKind.Progress, Progress = payload };

    public static WorkerFrame Of(ObserverPhasePayload payload)
        => new() { Kind = WorkerFrameKind.ObserverPhase, ObserverPhase = payload };

    public static WorkerFrame Of(ObserverSamplesPayload payload)
        => new() { Kind = WorkerFrameKind.ObserverSamples, ObserverSamples = payload };

    public static WorkerFrame Of(ObserverDetectorPayload payload)
        => new() { Kind = WorkerFrameKind.ObserverDetector, ObserverDetector = payload };

    public static WorkerFrame Of(BenchmarkCompletedPayload payload)
        => new() { Kind = WorkerFrameKind.BenchmarkCompleted, BenchmarkCompleted = payload };

    public static WorkerFrame Of(GroupCompletedPayload payload)
        => new() { Kind = WorkerFrameKind.GroupCompleted, GroupCompleted = payload };

    public static WorkerFrame Of(FaultPayload payload)
        => new() { Kind = WorkerFrameKind.Fault, Fault = payload };

    public static WorkerFrame Shutdown() => new() { Kind = WorkerFrameKind.Shutdown };
}

internal sealed record HandshakePayload
{
    public required int ProtocolVersion { get; init; }

    /// <summary>Diagnostics only - see <see cref="WorkerProtocol.ParentProcessIdArgument" />.</summary>
    public required int ParentProcessId { get; init; }
}

/// <summary>
///     What the worker reports about itself once it is up. Every field is read from the
///     worker's own state rather than echoed back from the request, so the coordinator learns
///     what is <i>true</i> of the measuring process rather than what it asked for.
/// </summary>
internal sealed record ReadyPayload
{
    public required int ProtocolVersion { get; init; }
    public required int WorkerProcessId { get; init; }

    /// <summary>
    ///     The runtime profile the worker is running under, read from its own environment via
    ///     <see cref="Engine.RuntimeProfileEnvironment" />. This is the value stamped on results.
    /// </summary>
    public required string RuntimeProfileName { get; init; }

    /// <summary>The startup knobs actually in effect, formatted by <see cref="RuntimeProfile.Describe()" />.</summary>
    public required string RuntimeKnobs { get; init; }

    /// <summary>
    ///     <c>true</c> when a profile was deliberately applied at launch. A worker should never
    ///     report <c>false</c> - if it does, the coordinator failed to set the environment block
    ///     and the whole point of the process boundary was lost, which is worth surfacing.
    /// </summary>
    public required bool RuntimeProfileApplied { get; init; }

    /// <summary>The worker's own target framework, e.g. <c>net10.0</c>.</summary>
    public required string TargetFramework { get; init; }

    /// <summary>
    ///     The <c>NBenchmark</c> version the worker will measure with. The coordinator compares
    ///     this against its own: because the worker unifies <c>NBenchmark</c> from its default
    ///     load context rather than loading the target's copy, a version skew would silently
    ///     measure against different engine code than the user compiled against.
    /// </summary>
    public required string EngineVersion { get; init; }

    public required string ProcessArchitecture { get; init; }
}

/// <summary>
///     One comparison group to measure. The unit is the group rather than the individual
///     benchmark on purpose: running the whole group in one worker makes every ratio a paired,
///     within-process estimate, so that worker's CPU draw, thermal state and address-space
///     layout cancel out of the ratio instead of inflating its variance.
/// </summary>
internal sealed record RunGroupPayload
{
    public required string GroupId { get; init; }

    public required WorkGroupKind Kind { get; init; }

    /// <summary>
    ///     The assembly the worker loads to resolve benchmarks from. This is the assembly that
    ///     <i>defines</i> them, which is not necessarily the entry assembly - under a test host
    ///     the entry assembly is the test runner.
    /// </summary>
    public required string TargetAssemblyPath { get; init; }

    /// <summary>
    ///     <see cref="WorkGroupKind.DiscoveredClass" />: the class to discover benchmarks on.
    ///     <see cref="WorkGroupKind.Plan" />: the type declaring the <c>[BenchmarkPlan]</c> factory.
    /// </summary>
    public string? DeclaringTypeFullName { get; init; }

    /// <summary>
    ///     <see cref="WorkGroupKind.DiscoveredClass" /> and <see cref="WorkGroupKind.Plan" />:
    ///     which of the discovered benchmarks to keep. Empty runs all of them.
    /// </summary>
    public IReadOnlyList<string> BenchmarkNames { get; init; } = [];

    /// <summary><see cref="WorkGroupKind.Lambdas" />: the addressed bodies, in declaration order.</summary>
    public IReadOnlyList<BodyRef> Bodies { get; init; } = [];

    /// <summary>
    ///     <see cref="WorkGroupKind.Plan" />: the factory method's name, resolved against
    ///     <see cref="DeclaringTypeFullName" /> rather than by metadata token.
    ///     <para>
    ///         Name resolution exists for multi-runtime runs, where the assembly under test is a
    ///         <i>different build</i> of the same source. A metadata token is only meaningful within
    ///         the build that produced it - and the module version id that guards against a stale
    ///         token differs between two target frameworks' builds by construction, so token
    ///         addressing cannot be made safe across them. A fully-qualified name is stable.
    ///     </para>
    ///     <para>
    ///         <c>null</c> for same-build runs, which address by token and get the stronger guarantee
    ///         that the method is precisely the one the caller passed.
    ///     </para>
    /// </summary>
    public string? PlanMethodName { get; init; }

    /// <summary>
    ///     <see cref="WorkGroupKind.TestMethod" />: the methods under test, in the order the caller
    ///     listed them.
    ///     <para>
    ///         A list rather than a single method because a <c>[Performance]</c> test that names a
    ///         <c>ReferenceMethod</c> sends <b>both</b> in one group, so that each replicate measures
    ///         the pair co-resident in one worker and their ratio is paired - the same property the
    ///         group-per-worker rule buys every other comparison. Measuring them in two workers leaves
    ///         each worker's core draw and address-space layout in the numerator and denominator
    ///         independently, and costs twice the wall clock to do it.
    ///     </para>
    /// </summary>
    public IReadOnlyList<TestMethodPayload> TestMethods { get; init; } = [];

    /// <summary>
    ///     <see cref="WorkGroupKind.TestMethod" />: the defining module's MVID, checked before any
    ///     token is trusted.
    ///     <para>
    ///         The same gate the lambda path uses, and mandatory for the same reason: deterministic
    ///         builds keep a token valid across a rebuild that inserted a method above it, so a
    ///         stale token addresses a <i>different</i> method and reports it under the right name.
    ///     </para>
    ///     <para>
    ///         One value for the group, not one per method: every method in a
    ///         <see cref="WorkGroupKind.TestMethod" /> group is declared by the same type, so they
    ///         share a module by construction.
    ///     </para>
    /// </summary>
    public Guid TestMethodModuleVersionId { get; init; }

    /// <summary>
    ///     Which <c>nbworker</c> to launch. <c>null</c> uses the one deployed beside this
    ///     application, which is right for everything measured against the running build.
    ///     <para>
    ///         Set for multi-runtime runs. A worker is a framework-dependent assembly, so measuring a
    ///         net8.0 build requires the net8.0 worker - the one this net10.0 coordinator sits beside
    ///         could not load that build's assemblies at all. The build targets already deploy the
    ///         correct worker into each target framework's output directory, so this is simply the
    ///         path to the one that was built alongside the code under test.
    ///     </para>
    /// </summary>
    public string? WorkerAssemblyPath { get; init; }

    /// <summary>
    ///     The measurement configuration, serialized whole. Everything on
    ///     <see cref="MeasurementOptions" /> is value data except the two strategy interfaces,
    ///     which travel as <see cref="OutlierDetectorTypeName" /> and
    ///     <see cref="SignificanceTestTypeName" />.
    ///     <para>
    ///         A request is always <b>one</b> measurement pass over the group. The replicate count is
    ///         spent by the coordinator, which sends one request per replicate - so nothing here says
    ///         how many launches were asked for, and there is no field a worker could act on and
    ///         double the run. See <see cref="LaunchCounts" />; the replicate index is in
    ///         <see cref="GroupId" /> and its distinct shuffle in <see cref="Seed" />.
    ///     </para>
    /// </summary>
    public required MeasurementOptions Options { get; init; }

    /// <summary>
    ///     Assembly-qualified type name of a custom <see cref="Stats.IOutlierDetector" />, which
    ///     the worker instantiates through its load context. <c>null</c> uses the built-in
    ///     detector for <see cref="MeasurementOptions.OutlierMode" />.
    /// </summary>
    public string? OutlierDetectorTypeName { get; init; }

    /// <summary>Assembly-qualified type name of a custom <see cref="Stats.ISignificanceTest" />.</summary>
    public string? SignificanceTestTypeName { get; init; }

    public RunOrder Order { get; init; } = RunOrder.Declaration;

    /// <summary>
    ///     The shuffle seed for this replicate. Each replicate gets a distinct seed derived from
    ///     the session seed, so run order becomes a randomized nuisance factor rather than a
    ///     fixed confound - unlike the previous isolated path, which hardcoded declaration order
    ///     and silently discarded <see cref="RunOrder.Random" /> whenever isolation was on.
    /// </summary>
    public int? Seed { get; init; }

    /// <summary>Prefix applied to benchmark display names, mirroring the previous isolated path.</summary>
    public string DisplayPrefix { get; init; } = "";

    /// <summary>
    ///     The harness-level default instance lifetime, which discovery needs and cannot infer -
    ///     it comes from <c>WithInstanceLifetime</c> on the coordinator side, not from the
    ///     benchmark class. A class-level <c>[InstanceLifetime]</c> attribute still wins, and that
    ///     is decided inside discovery in the worker.
    /// </summary>
    public InstanceLifetime DefaultInstanceLifetime { get; init; } = InstanceLifetime.PerMethod;

    /// <summary>
    ///     Total benchmarks across the whole run and this group's offset within it, so the
    ///     worker's progress callbacks carry indices that make sense to the coordinator's
    ///     progress UI rather than restarting at 1 for every group.
    /// </summary>
    public int StartIndex { get; init; }

    public int TotalBenchmarks { get; init; }

    /// <summary>
    ///     Whether the worker should also measure <see cref="CalibrationStandard" /> and return it on
    ///     the <see cref="GroupCompletedPayload" />.
    /// </summary>
    /// <remarks>
    ///     Requested by <see cref="WorkGroupKind.TestMethod" /> groups whose gate ratios against the
    ///     calibration rather than a named reference method. The point is that the divisor is measured
    ///     in the same process as the candidate, under the same runtime configuration - a calibration
    ///     measured in the test host would be running with tiering and ReadyToRun on while the
    ///     candidate ran with both off, and that difference alone is worth ~3.3x.
    /// </remarks>
    public bool MeasureCalibration { get; init; }
}

/// <summary>
///     An <see cref="IBenchmarkProgress" /> callback, flattened. Replayed into the user's real
///     progress instance by the coordinator - the previous isolated path had no channel for
///     this at all, so children ran silently and reporters never fired inside them.
/// </summary>
internal sealed record ProgressPayload
{
    public required ProgressCallback Callback { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Iteration or benchmark index, depending on <see cref="Callback" />.</summary>
    public int Index { get; init; }

    /// <summary>Total count, or non-positive when the engine has not resolved one (indeterminate).</summary>
    public int Total { get; init; }
}

internal enum ProgressCallback
{
    WarmupStarting = 0,
    WarmupCompleted = 1,
    BenchmarkStarting = 2,
    IterationCompleted = 3,
}

/// <summary>A <see cref="MeasurementPhaseEvent" />, flattened for the wire.</summary>
internal sealed record ObserverPhasePayload
{
    public required MeasurementPhase Phase { get; init; }
    public required PhaseTransition Transition { get; init; }
    public string BenchmarkName { get; init; } = "";
    public double? JitterMetric { get; init; }
    public bool DetectorSwitched { get; init; }
    public int? ResolvedK { get; init; }
    public int? ResolvedWarmup { get; init; }
    public WarmupStopReason? WarmupStop { get; init; }
    public SampleStopReason? SampleStop { get; init; }
    public bool Succeeded { get; init; } = true;
}

/// <summary>
///     A batch of per-sample observer events, in the order the engine emitted them.
/// </summary>
/// <remarks>
///     <para>
///         Batched because this is the one frame whose volume is a function of how fast the
///         benchmarked code is. Every other worker-to-coordinator frame is emitted a handful of
///         times per benchmark; samples arrive in the thousands, and one frame each would put the
///         cost of observing the run inside the run - which is the opposite of what a worker is
///         for, and why the stream did not cross the boundary at all before.
///     </para>
///     <para>
///         The engine already throttles <see cref="IMeasurementObserver.OnSample" /> to roughly
///         every fiftieth sample, so what is batched here is that emitted subset rather than every
///         reading. It is unrelated to <see cref="MeasurementOptions.MaxRawSamples" />: that bounds
///         the array a <see cref="BenchmarkResult" /> carries, this is the live stream, and the two
///         are selected by different rules for different consumers.
///     </para>
/// </remarks>
internal sealed record ObserverSamplesPayload
{
    public required IReadOnlyList<ObserverSampleEntry> Samples { get; init; }
}

/// <summary>
///     One <see cref="SampleEvent" />, flattened for the wire. A struct so buffering a batch of
///     them costs one array rather than one object per sample.
/// </summary>
internal readonly record struct ObserverSampleEntry(
    string BenchmarkName,
    int Ordinal,
    double PerOpNs,
    int K,
    long AllocDelta,
    bool Warmup);

/// <summary>A <see cref="DetectorStateEvent" />, flattened for the wire.</summary>
internal sealed record ObserverDetectorPayload
{
    public required string BenchmarkName { get; init; }
    public required MeasurementPhase Phase { get; init; }
    public required int SampleCount { get; init; }
    public required double Mean { get; init; }
    public required double StdDev { get; init; }
    public required double CiHalfWidth { get; init; }
    public required int CurrentK { get; init; }
}

/// <summary>
///     One finished benchmark. The raw samples ride <b>inside</b> this frame, next to the
///     result they belong to, rather than in a side dictionary the receiver looks them up in.
///     That is a deliberate structural choice: the previous design keyed samples in a separate
///     map, two call sites disagreed about the key format, and every isolated result silently
///     lost its samples - which disabled significance testing in the default mode. Here there
///     is no key, so there is nothing to mismatch.
/// </summary>
internal sealed record BenchmarkCompletedPayload
{
    public required BenchmarkResult Result { get; init; }
    public required double[] RawSamples { get; init; }
}

internal sealed record GroupCompletedPayload
{
    public required string GroupId { get; init; }

    /// <summary>
    ///     The worker's own measurement of <see cref="CalibrationStandard" />, when
    ///     <see cref="RunGroupPayload.MeasureCalibration" /> asked for one.
    /// </summary>
    /// <remarks>
    ///     On the terminal frame rather than beside a result, because it belongs to the process
    ///     rather than to any one benchmark - and because it is measured after the group's work, when
    ///     the process is in the state the group left it.
    /// </remarks>
    public CalibrationPayload? Calibration { get; init; }
}

/// <summary>A worker-measured <see cref="CalibrationResult" />, flattened for the wire.</summary>
internal sealed record CalibrationPayload
{
    public required double Mean { get; init; }
    public required double Median { get; init; }
    public required double[] Samples { get; init; }

    public CalibrationResult ToResult() => new(Mean, Median, Samples);
}

internal sealed record FaultPayload
{
    public required string Message { get; init; }
    public string? Detail { get; init; }

    /// <summary>
    ///     The benchmark the fault is attributable to, when it is attributable to one (a body
    ///     that could not be addressed). <c>null</c> means the whole group failed.
    /// </summary>
    public string? BenchmarkName { get; init; }
}

/// <summary>
///     One method a <see cref="WorkGroupKind.TestMethod" /> group measures.
/// </summary>
/// <remarks>
///     <see cref="DisplayName" /> travels with the token rather than being derived on either side.
///     The worker names its results from it, so a group carrying several methods can be mapped back
///     to the caller's own names without either side reconstructing a naming rule the other applies -
///     the class-prefixing convention discovery uses is not the one a test integration wants, and two
///     copies of it would be free to drift.
/// </remarks>
internal sealed record TestMethodPayload
{
    /// <summary>Metadata token of the method, within <see cref="RunGroupPayload.TargetAssemblyPath" />.</summary>
    public required int Token { get; init; }

    /// <summary>The name the caller wants results reported under.</summary>
    public required string DisplayName { get; init; }

    /// <summary>
    ///     The test case's argument values, in declaration order.
    ///     <para>
    ///         Only simple values ever reach here - the coordinator refuses to route a test whose
    ///         arguments are live objects, rather than reconstructing them and measuring something
    ///         subtly different.
    ///     </para>
    /// </summary>
    public IReadOnlyList<TestArgumentPayload> Arguments { get; init; } = [];
}

/// <summary>
///     One argument value for a test method, in a form that survives the process boundary.
/// </summary>
/// <remarks>
///     Carried as an invariant-culture string plus its type name rather than as JSON, because the
///     set of permitted types is deliberately small and closed - primitives, strings, enums and a
///     few unambiguous value types. A general object serializer here would quietly widen that set to
///     "whatever happens to round-trip", which is exactly the mechanism that is right most of the
///     time and silently wrong the rest.
/// </remarks>
internal sealed record TestArgumentPayload
{
    /// <summary>Assembly-qualified name of the argument's declared parameter type.</summary>
    public required string TypeName { get; init; }

    /// <summary>Invariant-culture text form, or <c>null</c> for a null argument.</summary>
    public string? Value { get; init; }
}
