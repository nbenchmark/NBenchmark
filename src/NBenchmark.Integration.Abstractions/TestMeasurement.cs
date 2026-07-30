using System.Reflection;
using NBenchmark.Engine;
using NBenchmark.Stats;
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

    /// <summary>One method to measure, and the name to report it under.</summary>
    public readonly record struct Target(MethodInfo Method, object?[] Arguments, string Name);

    /// <summary>
    ///     A candidate and the reference it is compared against, plus the paired ratio between them
    ///     when the two were measured in a way that admits one.
    /// </summary>
    /// <param name="PairedRatio">
    ///     The per-replicate ratio with its confidence interval, or <c>null</c> when this pair cannot
    ///     produce one - a single replicate, or a fallback to the test host.
    ///     <para>
    ///         Produced <b>here</b> rather than by the gate that consumes it, because only this method
    ///         knows the two measurements were co-resident: replicate <i>i</i> of both ran in the same
    ///         worker, which is the assumption the pairing rests on. A gate handed two results and left to
    ///         pair them by launch index could not tell that pair apart from two results measured in
    ///         separate workers, where the same arithmetic reports the difference between two processes
    ///         as a property of the code.
    ///     </para>
    /// </param>
    public readonly record struct MeasuredPair(
        Measured Candidate,
        Measured? Reference,
        RatioEstimate? PairedRatio = null);

    /// <summary>
    ///     Measures a candidate and its reference method <b>together</b>, one worker per replicate.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The reason this exists as one call rather than two: a ratio between two measurements taken
    ///         in different processes carries both processes' differences in it. Measured in one worker per
    ///         replicate, the pair's ratio has that worker's core draw, thermal state and address-space
    ///         layout divided out - and with <paramref name="launchCount" /> (that is,
    ///         <see cref="IPerformanceThresholds.LaunchCount" />) above one, the spread of those
    ///         per-replicate ratios becomes the interval a gate can be judged against. It is also half the
    ///         work: <i>n</i> workers rather than <i>2n</i>.
    ///     </para>
    ///     <para>
    ///         Falls back to the test host for both sides when the pair cannot be isolated, and says why.
    ///         The fallback measures them separately, because there is only one process to measure them
    ///         in - and reports no paired ratio, since the host's inherited JIT state makes a sequence of
    ///         measurements there something other than replicates.
    ///     </para>
    /// </remarks>
    /// <param name="reference">
    ///     The method to compare against, or <c>null</c> when the test names none - in which case this is
    ///     an ordinary single measurement and the gate divides by the calibration standard instead.
    /// </param>
    /// <param name="launchCount">
    ///     How many replicates to measure, one worker each. Passed separately from
    ///     <paramref name="runSpec" /> because a launch is a process rather than a property of a
    ///     measurement - see <see cref="LaunchCounts" />. Above one is what produces
    ///     <see cref="MeasuredPair.PairedRatio" />.
    /// </param>
    public static async Task<MeasuredPair> MeasurePairAsync(
        Target candidate,
        Target? reference,
        object? instance,
        RunSpec runSpec,
        int launchCount,
        CancellationToken cancellationToken = default,
        bool measureCalibration = false)
    {
        ArgumentNullException.ThrowIfNull(candidate.Method);

        if (reference is not { } referenceTarget)
        {
            var single = await MeasureAsync(
                    candidate.Method, instance, candidate.Arguments, candidate.Name, runSpec,
                    launchCount, cancellationToken, measureCalibration)
                .ConfigureAwait(false);

            return new MeasuredPair(single, null);
        }

        var subjects = new[]
        {
            new TestMethodRunner.Subject(candidate.Method, candidate.Arguments, candidate.Name),
            new TestMethodRunner.Subject(referenceTarget.Method, referenceTarget.Arguments, referenceTarget.Name),
        };

        var refusal = CanIsolate(candidate, instance)
                      ?? CanIsolate(referenceTarget, instance);

        if (refusal is null)
        {
            var outcome = await TestMethodRunner
                .RunAsync(subjects, runSpec.Options, launchCount, cancellationToken, measureCalibration)
                .ConfigureAwait(false);

            if (outcome.Measurements.Count == subjects.Length)
            {
                var measuredCandidate = outcome.Measurements[0];
                var measuredReference = outcome.Measurements[1];

                return new MeasuredPair(
                    new Measured(
                        measuredCandidate.Result, measuredCandidate.RawSamples, null, outcome.Calibration),
                    new Measured(measuredReference.Result, measuredReference.RawSamples, null),
                    LogRatio.Estimate(measuredCandidate.Result, measuredReference.Result));
            }

            // The worker was available but did not deliver. Measuring in the host is better than
            // failing the test over infrastructure, provided the result says so.
            refusal = outcome.Refusal;
        }

        // Straight to the host for both, rather than through MeasureAsync, which would try a worker
        // again per side. Retrying it here could isolate one side and not the other - the one
        // arrangement no ratio gate will enforce - after spending the launches to get there.
        var hostCandidate = await MeasureInHostAsync(candidate, instance, runSpec, refusal, cancellationToken)
            .ConfigureAwait(false);

        var hostReference = await MeasureInHostAsync(referenceTarget, instance, runSpec, refusal, cancellationToken)
            .ConfigureAwait(false);

        return new MeasuredPair(hostCandidate, hostReference);
    }

    /// <summary>
    ///     Whether one target can be measured in a worker, or the reason it cannot.
    /// </summary>
    /// <remarks>
    ///     A pair is isolated together or not at all: one side in a worker and the other in the host is
    ///     the arrangement that fabricated a 2.80x ratio between bodies of identical cost, and the gate
    ///     declines to enforce it anyway. So the first refusal from either side sends both to the host.
    /// </remarks>
    private static string? CanIsolate(Target target, object? instance)
    {
        var decision = TestBodyIsolation.Classify(target.Method, instance, target.Arguments);

        if (!decision.CanIsolate)
            return decision.Reason;

        return TestMethodRunner.CanAddress(target.Method, out var addressRefusal) ? null : addressRefusal;
    }

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
        int launchCount,
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
                .RunAsync(method, args, name, runSpec.Options, launchCount, cancellationToken, measureCalibration)
                .ConfigureAwait(false);

            if (outcome.Measured)
                return new Measured(outcome.Result!, [.. outcome.RawSamples], null, outcome.Calibration);

            // The worker was available but did not deliver. Measuring in the host is better than
            // failing the test over infrastructure, provided the result says so.
            refusal = outcome.Refusal;
        }

        return await MeasureInHostAsync(
                new Target(method, args, name), instance, runSpec, refusal, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Measures in the test host, stamped with why it was not measured in a worker.
    /// </summary>
    /// <remarks>
    ///     The classification is redone here rather than passed in, so the status on the result is derived
    ///     from the same question every time. A caller threading its own status through is how one path
    ///     comes to stamp <see cref="IsolationStatus.Isolated" /> on a host measurement, which is the one
    ///     label that must never be wrong - it is what <c>RequireIsolation</c> and the ratio gate read.
    /// </remarks>
    private static async Task<Measured> MeasureInHostAsync(
        Target target,
        object? instance,
        RunSpec runSpec,
        string? refusal,
        CancellationToken cancellationToken)
    {
        var decision = TestBodyIsolation.Classify(target.Method, instance, target.Arguments);

        var status = decision.CanIsolate
            ? IsolationStatus.InProcessNoWorker
            : StatusFor(decision.Status);

        var host = await RunInHostAsync(
                target.Method, instance, target.Arguments, target.Name, runSpec, cancellationToken)
            .ConfigureAwait(false);

        return new Measured(
            host.Result with { IsolationStatus = status },
            host.RawSamples,
            refusal ?? decision.Reason);
    }

    private static async Task<Measured> RunInHostAsync(
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
