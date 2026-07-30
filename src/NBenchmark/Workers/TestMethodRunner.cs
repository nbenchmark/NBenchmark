using System.Reflection;
using NBenchmark.Engine;

namespace NBenchmark.Workers;

/// <summary>
///     Measures a test-framework method in a worker process, or reports why it could not be.
/// </summary>
/// <remarks>
///     The entry point the xUnit, NUnit and MSTest integrations share. It exists in the core
///     assembly rather than in <c>NBenchmark.Integration.Abstractions</c> because the worker
///     protocol is internal to the core - the integrations should be able to ask for an isolated
///     measurement without being able to hand-assemble a wire request.
/// </remarks>
public static class TestMethodRunner
{
    /// <summary>One method to measure, and the name to report it under.</summary>
    /// <param name="Arguments">
    ///     The test case's argument values, encoded against each parameter's <i>declared</i> type.
    /// </param>
    public readonly record struct Subject(MethodInfo Method, object?[] Arguments, string DisplayName);

    /// <summary>What one subject measured.</summary>
    /// <param name="RawSamples">
    ///     Samples <b>pooled across replicates</b>, for a significance test that wants every
    ///     observation. Deliberately not the same array as <see cref="BenchmarkResult.RawSamples" />
    ///     on <paramref name="Result" />, which is one representative launch's - the trim ordinals on
    ///     the result index into that array, and marks against a pooled one would point at the wrong
    ///     samples. This is the same split the engine's own aggregation makes.
    /// </param>
    public readonly record struct Measurement(string Name, BenchmarkResult Result, double[] RawSamples);

    /// <summary>The outcome of an attempt to measure one or more test methods out of process.</summary>
    /// <param name="Measurements">
    ///     One entry per requested subject, <b>in request order</b>, or empty when the caller must
    ///     measure them itself. A subject the worker never reported is present and errored rather than
    ///     missing, so the list cannot be shorter than the request and no index can address the wrong
    ///     subject.
    /// </param>
    /// <param name="Refusal">
    ///     Why measurement did not happen out of process, when it did not. Present for both a
    ///     refusal and a worker failure - the caller falls back either way, and the distinction
    ///     belongs in the message rather than in the control flow.
    /// </param>
    /// <param name="Calibration">
    ///     The calibration standard as measured <i>inside the worker</i>, when one was asked for.
    ///     <c>null</c> when it was not requested, or when no launch could produce one.
    ///     <para>
    ///         A gate that ratios against the calibration rather than a named reference method needs
    ///         its divisor measured under the same runtime configuration as the candidate. Measured in
    ///         the test host it would not be: the host runs with tiering and ReadyToRun on, the worker
    ///         with both off, and that difference alone moves a body of identical cost by ~3.3x.
    ///     </para>
    ///     <para>
    ///         With replicates, <see cref="CalibrationResult.LaunchMedians" /> carries one median per
    ///         launch, so the calibration ratio is paired the same way a reference-method ratio is.
    ///     </para>
    /// </param>
    public readonly record struct Outcome(
        IReadOnlyList<Measurement> Measurements,
        string? Refusal,
        CalibrationResult? Calibration = null)
    {
        public bool Measured => Measurements.Count > 0;

        /// <summary>The first subject's result - by convention the one under test.</summary>
        public BenchmarkResult? Result => Measurements.Count > 0 ? Measurements[0].Result : null;

        /// <summary>The first subject's pooled samples.</summary>
        public IReadOnlyList<double> RawSamples => Measurements.Count > 0 ? Measurements[0].RawSamples : [];
    }

    /// <summary>
    ///     Whether a method's arguments and declaring assembly can be addressed at all.
    /// </summary>
    /// <remarks>
    ///     Kept separate from <c>TestBodyIsolation.Classify</c>, which answers the framework-facing
    ///     question (is this instance rebuildable). This answers the transport-facing one: can each
    ///     argument survive the wire. A caller that has already classified still needs this, because
    ///     an argument's <i>declared parameter type</i> - not its runtime type - is what has to be
    ///     reconstructible, and only the method knows that.
    /// </remarks>
    /// <param name="options">
    ///     The measurement configuration, so a pinned outlier detector or significance test that a
    ///     worker cannot rebuild is caught here instead of being silently replaced by the built-in one
    ///     on the far side. <c>null</c> skips that check.
    /// </param>
    public static bool CanAddress(MethodInfo method, out string? refusal, MeasurementOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(method);

        refusal = null;

        var declaringAssemblyLocation = method.DeclaringType?.Assembly.Location;

        if (!WorkerLauncher.Current.IsAvailableFor(declaringAssemblyLocation))
        {
            refusal = "the measurement worker (nbworker) is not deployed alongside these tests. "
                      + $"Looked in {WorkerLocator.DescribeSearch(declaringAssemblyLocation)}.";

            return false;
        }

        foreach (var parameter in method.GetParameters())
        {
            if (TestArgumentCodec.IsSupported(parameter.ParameterType))
                continue;

            refusal = $"parameter '{parameter.Name}' is of type '{parameter.ParameterType.Name}', which "
                      + "cannot be rebuilt in another process. Simple values travel; object graphs do not.";

            return false;
        }

        if (options is not null && WorkerRunPlan.UnrebuildableStrategy(options) is { } strategyRefusal)
        {
            refusal = strategyRefusal;

            return false;
        }

        return true;
    }

    /// <summary>Measures <paramref name="method" /> in a worker.</summary>
    /// <param name="measureCalibration">
    ///     Whether the worker should also measure <see cref="CalibrationStandard" /> and return it on
    ///     <see cref="Outcome.Calibration" />. Ask for it only when the gate will use it - it is
    ///     cheap, but it is not free, and a test that names a reference method has no use for it.
    /// </param>
    public static Task<Outcome> RunAsync(
        MethodInfo method,
        object?[] arguments,
        string displayName,
        MeasurementOptions options,
        CancellationToken cancellationToken = default,
        bool measureCalibration = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);

        return RunAsync([new Subject(method, arguments, displayName)], options, cancellationToken, measureCalibration);
    }

    /// <summary>
    ///     Measures every subject in one worker per replicate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why the subjects share a worker.</b> A test that compares against a reference method
    ///         sends both here, and each replicate measures the pair co-resident in one process. Their
    ///         per-replicate ratio then has that worker's core draw, thermal state and address-space
    ///         layout divided out of it - the same reason the engine runs a comparison group in one
    ///         worker. Measuring them in two workers would leave all of that in the ratio and cost twice
    ///         the wall clock to do it.
    ///     </para>
    ///     <para>
    ///         <b>Why replicates are spent here.</b> <see cref="MeasurementOptions.LaunchCount" /> is the
    ///         number of <i>workers</i>, so it is spent by this method and pinned to 1 in the request. A
    ///         worker that repeated the measurement internally would report within-process precision as
    ///         though it were reproducibility, which is the one thing a replicate is for. Above one
    ///         replicate the results carry <see cref="BenchmarkResult.LaunchStatistics" /> and the ratio
    ///         gate becomes a paired estimate with an interval; at one - the default - nothing about the
    ///         result changes, so an existing suite is neither slower nor differently judged.
    ///     </para>
    /// </remarks>
    /// <param name="subjects">
    ///     The methods to measure. The first is the one under test; any others are references it is
    ///     compared against. All must be declared by the same type, because the worker instantiates one.
    /// </param>
    public static async Task<Outcome> RunAsync(
        IReadOnlyList<Subject> subjects,
        MeasurementOptions options,
        CancellationToken cancellationToken = default,
        bool measureCalibration = false)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(options);

        if (subjects.Count == 0)
            throw new ArgumentException("At least one subject is required.", nameof(subjects));

        ValidateGroup(subjects);

        foreach (var subject in subjects)
        {
            if (!CanAddress(subject.Method, out var refusal, options))
                return new Outcome([], refusal);
        }

        var declaringType = subjects[0].Method.DeclaringType!;
        var replicates = Math.Max(1, options.LaunchCount);
        var timeout = MeasurementBudget.For(options, subjects.Count);

        // One list per subject, one entry per replicate - including replicates that produced nothing,
        // which are recorded as errored rather than skipped. That is what keeps a launch index meaning
        // the launch it names: a list that dropped its failures would line launch 2 of one subject up
        // against launch 3 of the other, and the paired ratio would then be a difference between two
        // processes reported as a property of the code.
        var launches = subjects
            .ToDictionary(s => s.DisplayName, _ => new List<LaunchAggregator.Launch>(replicates), StringComparer.Ordinal);

        var pooledSamples = subjects
            .ToDictionary(s => s.DisplayName, _ => new List<double>(), StringComparer.Ordinal);

        var calibrationMedians = new double[replicates];
        CalibrationResult? firstCalibration = null;
        var calibrationSum = 0.0;
        var calibrationMeanSum = 0.0;
        var calibrationCount = 0;

        for (var replicate = 0; replicate < replicates; replicate++)
        {
            var request = BuildRequest(declaringType, subjects, options, replicate, measureCalibration);

            var group = await WorkerLauncher.Current.RunGroupAsync(
                    request,
                    NullBenchmarkProgress.Instance,
                    NullMeasurementObserver.Instance,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);

            // Nothing at all, on the first replicate, is an infrastructure failure rather than a
            // measurement: the caller falls back to the test host and says why. A later replicate
            // failing is a partial result, and is recorded as an errored launch below.
            if (group.Results.Count == 0 && replicate == 0)
            {
                return new Outcome(
                    [],
                    group.Faults.FirstOrDefault()?.Message ?? "the measurement worker returned no result.");
            }

            var reported = group.Results.ToDictionary(r => r.Name, StringComparer.Ordinal);

            foreach (var subject in subjects)
            {
                if (reported.TryGetValue(subject.DisplayName, out var result))
                {
                    var samples = group.RawSamples.GetValueOrDefault(subject.DisplayName, []);
                    launches[subject.DisplayName].Add(new LaunchAggregator.Launch(result, samples));
                    pooledSamples[subject.DisplayName].AddRange(samples);

                    continue;
                }

                var message = group.Faults.FirstOrDefault(f => f.BenchmarkName == subject.DisplayName)?.Message
                              ?? group.Faults.FirstOrDefault()?.Message
                              ?? $"the measurement worker returned no result for '{subject.DisplayName}'.";

                launches[subject.DisplayName].Add(new LaunchAggregator.Launch(
                    WorkerGroupRunner.ErroredResult(subject.DisplayName, message),
                    []));
            }

            if (group.Calibration is { } calibration)
            {
                calibrationMedians[replicate] = calibration.Median;
                calibrationMeanSum += calibration.Mean;
                calibrationSum += calibration.Median;
                calibrationCount++;
                firstCalibration ??= calibration;
            }
        }

        var measurements = subjects
            .Select(subject => new Measurement(
                subject.DisplayName,
                Combine(launches[subject.DisplayName], replicates),
                [.. pooledSamples[subject.DisplayName]]))
            .ToList();

        return new Outcome(measurements, null, CombineCalibration());

        CalibrationResult? CombineCalibration()
        {
            if (firstCalibration is null || calibrationCount == 0)
                return null;

            return firstCalibration with
            {
                Mean = calibrationMeanSum / calibrationCount,
                Median = calibrationSum / calibrationCount,

                // Only when there are launches to pair. A single-launch run reports an empty list
                // rather than a one-entry one, because one ratio is not an estimate of one.
                LaunchMedians = replicates > 1 ? calibrationMedians : [],
            };
        }
    }

    /// <summary>
    ///     Rejects a group a worker could not measure as one, or whose results could not be told apart.
    /// </summary>
    /// <remarks>
    ///     Both of these are caller mistakes rather than environmental refusals, so they throw instead of
    ///     sending the measurement to the test host: falling back would measure something - a method
    ///     against itself, or two classes' methods in one process - and report a ratio for it. Thrown from
    ///     here rather than discovered in the worker so the message names the two methods the caller passed.
    /// </remarks>
    private static void ValidateGroup(IReadOnlyList<Subject> subjects)
    {
        var declaringType = subjects[0].Method.DeclaringType;
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var subject in subjects)
        {
            if (subject.Method.DeclaringType != declaringType)
            {
                throw new ArgumentException(
                    $"'{subject.Method.Name}' is declared by '{subject.Method.DeclaringType?.Name}' but "
                    + $"'{subjects[0].Method.Name}' by '{declaringType?.Name}'. A paired comparison is "
                    + "measured in one worker, which builds one test-class instance, so both methods must "
                    + "belong to the same class.",
                    nameof(subjects));
            }

            if (!names.Add(subject.DisplayName))
            {
                throw new ArgumentException(
                    $"Two subjects are both named '{subject.DisplayName}', so their results could not be "
                    + "told apart. This usually means a reference method resolved to the method under test - "
                    + "a comparison against itself, which always reports 1.00x.",
                    nameof(subjects));
            }
        }
    }

    /// <summary>
    ///     The single row for a subject: its one launch, or the average of its replicates with the
    ///     between-launch interval.
    /// </summary>
    /// <remarks>
    ///     A single-replicate run is returned untouched rather than run through
    ///     <see cref="LaunchAggregator.Combine" />, so opting out of replicates leaves the result exactly
    ///     as it was before replicates existed - including carrying no
    ///     <see cref="BenchmarkResult.LaunchStatistics" />, which is what tells every downstream gate
    ///     that there is nothing to pair.
    /// </remarks>
    private static BenchmarkResult Combine(IReadOnlyList<LaunchAggregator.Launch> launches, int replicates)
    {
        var result = replicates > 1
            ? LaunchAggregator.Combine(launches)
            : launches[0].Result with { RawSamples = launches[0].RawSamples };

        // Errored rows are left alone: a measurement that never happened was not taken in a worker
        // either, and stamping it isolated would let it satisfy RequireIsolation.
        return result.Errored ? result : result with { IsolationStatus = IsolationStatus.Isolated };
    }

    private static RunGroupPayload BuildRequest(
        Type declaringType,
        IReadOnlyList<Subject> subjects,
        MeasurementOptions options,
        int replicate,
        bool measureCalibration)
    {
        var encoded = new TestMethodPayload[subjects.Count];

        for (var s = 0; s < subjects.Count; s++)
        {
            var subject = subjects[s];
            var parameters = subject.Method.GetParameters();
            var arguments = new TestArgumentPayload[parameters.Length];

            for (var i = 0; i < parameters.Length; i++)
            {
                // Encoded against the declared parameter type, not the value's runtime type: a long
                // parameter given the literal 1 arrives here boxed as an int, and sending Int32 would
                // bind the wrong thing on the far side.
                arguments[i] = TestArgumentCodec.Encode(parameters[i].ParameterType, subject.Arguments[i]);
            }

            encoded[s] = new TestMethodPayload
            {
                Token = subject.Method.MetadataToken,
                DisplayName = subject.DisplayName,
                Arguments = arguments,
            };
        }

        return new RunGroupPayload
        {
            GroupId = $"test:{declaringType.FullName}.{subjects[0].Method.Name}#{replicate}",
            Kind = WorkGroupKind.TestMethod,
            TargetAssemblyPath = declaringType.Assembly.Location,
            DeclaringTypeFullName = declaringType.FullName,
            TestMethodModuleVersionId = declaringType.Module.ModuleVersionId,
            TestMethods = encoded,
            BenchmarkNames = subjects.Select(s => s.DisplayName).ToList(),

            // LaunchCount is spent above by spawning one worker per replicate, so each worker measures
            // exactly once. Leaving it above 1 here would multiply the two.
            Options = options with { LaunchCount = 1 },
            OutlierDetectorTypeName = WorkerRunPlan.StrategyTypeName(options.OutlierDetector, out _),
            SignificanceTestTypeName = WorkerRunPlan.StrategyTypeName(options.SignificanceTest, out _),

            // Order matters only once there are two bodies in the group, and then it matters a lot:
            // measured in a fixed order every time, whichever runs first carries the cost of warming
            // shared state for the other, and that lands in the ratio as a property of the code. Each
            // replicate shuffles independently, which turns it into a nuisance factor that averages out.
            Order = subjects.Count > 1 ? RunOrder.Random : RunOrder.Declaration,
            TotalBenchmarks = subjects.Count,
            MeasureCalibration = measureCalibration,
        };
    }
}
