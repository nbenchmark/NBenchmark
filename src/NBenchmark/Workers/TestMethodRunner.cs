using System.Reflection;

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
    /// <summary>The outcome of an attempt to measure a test method out of process.</summary>
    /// <param name="Result">The measurement, or <c>null</c> when the caller must measure it itself.</param>
    /// <param name="Refusal">
    ///     Why measurement did not happen out of process, when it did not. Present for both a
    ///     refusal and a worker failure - the caller falls back either way, and the distinction
    ///     belongs in the message rather than in the control flow.
    /// </param>
    /// <param name="Calibration">
    ///     The calibration standard as measured <i>inside the worker</i>, when one was asked for.
    ///     <c>null</c> when it was not requested, or when the worker could not produce one.
    ///     <para>
    ///         A gate that ratios against the calibration rather than a named reference method needs
    ///         its divisor measured under the same runtime configuration as the candidate. Measured in
    ///         the test host it would not be: the host runs with tiering and ReadyToRun on, the worker
    ///         with both off, and that difference alone moves a body of identical cost by ~3.3x.
    ///     </para>
    /// </param>
    public readonly record struct Outcome(
        BenchmarkResult? Result,
        IReadOnlyList<double> RawSamples,
        string? Refusal,
        CalibrationResult? Calibration = null)
    {
        public bool Measured => Result is not null;
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
    public static async Task<Outcome> RunAsync(
        MethodInfo method,
        object?[] arguments,
        string displayName,
        MeasurementOptions options,
        CancellationToken cancellationToken = default,
        bool measureCalibration = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(options);

        if (!CanAddress(method, out var refusal, options))
            return new Outcome(null, [], refusal);

        var declaringType = method.DeclaringType!;
        var assemblyPath = declaringType.Assembly.Location;
        var parameters = method.GetParameters();

        var encoded = new TestArgumentPayload[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            // Encoded against the declared parameter type, not the value's runtime type: a long
            // parameter given the literal 1 arrives here boxed as an int, and sending Int32 would
            // bind the wrong thing on the far side.
            encoded[i] = TestArgumentCodec.Encode(parameters[i].ParameterType, arguments[i]);
        }

        var request = new RunGroupPayload
        {
            GroupId = $"test:{declaringType.FullName}.{method.Name}",
            Kind = WorkGroupKind.TestMethod,
            TargetAssemblyPath = assemblyPath,
            DeclaringTypeFullName = declaringType.FullName,
            TestMethodToken = method.MetadataToken,
            TestMethodModuleVersionId = declaringType.Module.ModuleVersionId,
            TestMethodArguments = encoded,
            BenchmarkNames = [displayName],

            // LaunchCount is spent by the caller, not the worker: a test gates on one measurement,
            // and a worker that repeated it internally would report within-process precision as
            // though it were reproducibility.
            Options = options with { LaunchCount = 1 },
            OutlierDetectorTypeName = WorkerRunPlan.StrategyTypeName(options.OutlierDetector, out _),
            SignificanceTestTypeName = WorkerRunPlan.StrategyTypeName(options.SignificanceTest, out _),
            TotalBenchmarks = 1,
            MeasureCalibration = measureCalibration,
        };

        var group = await WorkerLauncher.Current.RunGroupAsync(
                request,
                NullBenchmarkProgress.Instance,
                NullMeasurementObserver.Instance,
                MeasurementBudget.For(options, 1),
                cancellationToken)
            .ConfigureAwait(false);

        if (group.Results.Count == 0)
        {
            return new Outcome(
                null,
                [],
                group.Faults.FirstOrDefault()?.Message ?? "the measurement worker returned no result.");
        }

        var result = group.Results[0];
        var samples = group.RawSamples.GetValueOrDefault(result.Name, []);

        return new Outcome(
            result with { IsolationStatus = IsolationStatus.Isolated, RawSamples = samples },
            samples,
            null,
            group.Calibration);
    }
}
