using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Workers;

namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     Measures one test method, in a worker process when that is possible and in the test host when
///     it is not - saying which, either way.
/// </summary>
/// <remarks>
///     <para>
///         The single measurement entry point for all three test-framework integrations. They had
///         previously carried a copy each of the surrounding logic, and copies of measurement code
///         do not fail loudly when they drift - they measure slightly different things under the
///         same name.
///     </para>
///     <para>
///         Isolation matters most here. A test integration exists to <b>gate</b>, and a gate reading
///         a number from the test host is not being conservative: the host's JIT state is whatever
///         the preceding tests left behind, and on bodies of provably identical cost that fabricated
///         a 2.80x ratio with a tight confidence interval on each side.
///     </para>
/// </remarks>
public static class TestMeasurement
{
    /// <summary>A measurement, and where it was taken.</summary>
    /// <param name="Refusal">
    ///     Why the measurement was taken in the test host, when it was. <c>null</c> when isolated.
    /// </param>
    /// <param name="Calibration">
    ///     The calibration standard measured in the same worker as <paramref name="Result" />, when
    ///     one was asked for and produced. <c>null</c> otherwise, which sends a gate to the host's own
    ///     calibration - correct for a host-measured result, a compromise for an isolated one.
    /// </param>
    public readonly record struct Measured(
        BenchmarkResult Result,
        double[] RawSamples,
        string? Refusal,
        CalibrationResult? Calibration = null);

    /// <summary>
    ///     Measures <paramref name="method" />, preferring a worker.
    /// </summary>
    /// <param name="instance">
    ///     The live test-class instance. Used only for the in-host path and for deciding whether an
    ///     equivalent could be rebuilt elsewhere - it is never sent anywhere.
    /// </param>
    /// <param name="measureCalibration">
    ///     Whether the worker should also measure <see cref="CalibrationStandard" />. Ask for it when
    ///     the gate will divide by it - that is, when a <c>MaxSlowdownRatio</c> is set with no
    ///     reference method - so that both sides of the ratio come from the same process.
    /// </param>
    public static async Task<Measured> MeasureAsync(
        MethodInfo method,
        object? instance,
        object?[] args,
        string name,
        RunSpec runSpec,
        CancellationToken cancellationToken = default,
        bool measureCalibration = false)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(args);

        var decision = TestBodyIsolation.Classify(method, instance, args);

        // Two gates, deliberately distinct. Classify answers the framework question - could a worker
        // rebuild this instance - and CanAddress answers the transport one: can each declared
        // parameter type survive the wire. Only the method knows the latter.
        var refusal = decision.CanIsolate
            ? TestMethodRunner.CanAddress(method, out var addressRefusal) ? null : addressRefusal
            : decision.Reason;

        if (refusal is null)
        {
            var outcome = await TestMethodRunner
                .RunAsync(method, args, name, runSpec.Options, cancellationToken, measureCalibration)
                .ConfigureAwait(false);

            if (outcome.Measured)
                return new Measured(outcome.Result!, [.. outcome.RawSamples], null, outcome.Calibration);

            // The worker was available but did not deliver. Measuring in the host is better than
            // failing the test over infrastructure, provided the result says so.
            refusal = outcome.Refusal;
        }

        var status = decision.CanIsolate
            ? IsolationStatus.InProcessNoWorker
            : StatusFor(decision.Status);

        var host = await MeasureInHostAsync(method, instance, args, name, runSpec, cancellationToken)
            .ConfigureAwait(false);

        return new Measured(
            host.Result with { IsolationStatus = status },
            host.RawSamples,
            refusal);
    }

    private static async Task<Measured> MeasureInHostAsync(
        MethodInfo method,
        object? instance,
        object?[] args,
        string name,
        RunSpec runSpec,
        CancellationToken cancellationToken)
    {
        if (!TestBodyBuilder.TryBuild(method, instance, args, out var body, out var isAsync))
            throw new InvalidOperationException($"Could not build a benchmark body for '{method.Name}'.");

        if (isAsync)
        {
            var outcome = await BenchmarkRunner.Instance
                .RunAsync(name, (Func<Task>)body, runSpec, cancellationToken)
                .ConfigureAwait(false);

            return new Measured(outcome.Result, outcome.RawSamples, null);
        }

        var sync = BenchmarkRunner.Instance.Run(name, (Action)body, runSpec, cancellationToken);

        return new Measured(sync.Result, sync.RawSamples, null);
    }

    /// <summary>
    ///     Maps the classifier's status name onto the enum.
    /// </summary>
    /// <remarks>
    ///     An unrecognised name falls back to the vaguest honest answer rather than to
    ///     <see cref="IsolationStatus.Isolated" />. A wrong label here would let a host-measured row
    ///     satisfy <c>--strict-isolation</c>, which is the one thing the label exists to prevent.
    /// </remarks>
    private static IsolationStatus StatusFor(string status) => status switch
    {
        nameof(IsolationStatus.InProcessLiveFixture) => IsolationStatus.InProcessLiveFixture,
        nameof(IsolationStatus.InProcessUnaddressablePlan) => IsolationStatus.InProcessUnaddressablePlan,
        nameof(IsolationStatus.InProcessCapturedState) => IsolationStatus.InProcessCapturedState,
        _ => IsolationStatus.InProcessUnaddressablePlan,
    };
}
