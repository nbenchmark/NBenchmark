using System.Reflection;
using NBenchmark.Stats;

namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     Turns a measured result into pass/fail for a test-framework gate: absolute thresholds, the
///     isolation requirement, and the ratio comparison, in one place for all three integrations.
/// </summary>
/// <remarks>
///     <para>
///         The xUnit, NUnit and MSTest adapters each carried their own copy of this decision, and
///         so did the assert-pattern API. Copies of gate logic do not fail loudly when they drift -
///         they let one framework's users through a gate another framework's users fail.
///     </para>
///     <para>
///         The rule the copies did not have: <b>a ratio gate is enforced only between two
///         measurements taken the same way.</b> The runtime configuration a process starts with is
///         the dominant term in a small measurement - on bodies of provably identical cost it moved
///         the reported value by ~3.3x, and an in-host candidate/reference pair fabricated a 2.80x
///         ratio - so a ratio across that boundary reports the processes, not the code.
///     </para>
/// </remarks>
internal static class PerformanceGate
{
    /// <summary>
    ///     The result of a gate evaluation: <paramref name="Violations" /> fail the test,
    ///     <paramref name="Notes" /> belong in its output.
    /// </summary>
    /// <remarks>
    ///     Notes are not decoration. A gate that quietly declines to run is a gate that passes, and
    ///     the only thing standing between that and a missed regression is the note saying so.
    /// </remarks>
    public readonly record struct Outcome(IReadOnlyList<string> Violations, IReadOnlyList<string> Notes);

    /// <summary>
    ///     Whether <see cref="AllowInProcessGateAttribute" /> is declared on the test method, its
    ///     class, or its assembly.
    /// </summary>
    public static bool AllowsInProcessGate(MethodInfo? method)
    {
        if (method is null)
            return false;

        if (method.GetCustomAttribute<AllowInProcessGateAttribute>(inherit: true) is not null)
            return true;

        if (method.DeclaringType is not { } declaringType)
            return false;

        return declaringType.GetCustomAttribute<AllowInProcessGateAttribute>(inherit: true) is not null
               || declaringType.Assembly.GetCustomAttribute<AllowInProcessGateAttribute>() is not null;
    }

    /// <summary>
    ///     Whether this gate will divide by the calibration standard, and so whether a worker should
    ///     be asked to measure one.
    /// </summary>
    /// <remarks>
    ///     True only for a ratio gate with no reference method. A gate with a reference method
    ///     compares two of the user's own benchmarks and has no use for a calibration; a gate with no
    ///     ratio at all has nothing to divide.
    /// </remarks>
    public static bool NeedsCalibration(IPerformanceThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);

        return thresholds.MaxSlowdownRatio > 0 && string.IsNullOrWhiteSpace(thresholds.ReferenceMethod);
    }

    /// <summary>
    ///     Evaluates <paramref name="result" /> against <paramref name="thresholds" />.
    /// </summary>
    /// <param name="referenceResult">
    ///     The measured reference benchmark, or <c>null</c> when the test named none. Pass it even
    ///     when it was measured differently from the candidate - deciding what to do about that is
    ///     this method's job, and callers that pre-filtered it used to fall through to the
    ///     calibration comparison instead, which is a <i>worse</i> cross-process ratio than the one
    ///     they were avoiding.
    /// </param>
    /// <param name="workerCalibration">
    ///     The calibration standard as measured inside the same worker that produced
    ///     <paramref name="result" />, when there was one. Used in place of the test host's own
    ///     calibration so that both sides of a <c>MaxSlowdownRatio</c> ratio share a runtime
    ///     configuration. <c>null</c> falls back to the host measurement, which is the right
    ///     comparison when the benchmark also ran in the host.
    ///     <para>
    ///         With replicates its <see cref="CalibrationResult.LaunchMedians" /> makes the
    ///         calibration ratio paired as well, since each launch's divisor was measured in the same
    ///         process as that launch of the benchmark.
    ///     </para>
    /// </param>
    /// <param name="pairedRatio">
    ///     The per-replicate ratio of <paramref name="result" /> to <paramref name="referenceResult" />
    ///     with its interval, from <see cref="TestMeasurement.MeasurePairAsync" />. Pass it only when the
    ///     two were measured co-resident in the same worker per replicate - that co-residency is what
    ///     the pairing means, and it is not something this method can verify from two results.
    /// </param>
    public static Outcome Evaluate(
        BenchmarkResult result,
        IReadOnlyList<double>? rawSamples,
        BenchmarkResult? referenceResult,
        IReadOnlyList<double>? referenceSamples,
        IPerformanceThresholds thresholds,
        bool allowInProcessGate = false,
        CalibrationResult? workerCalibration = null,
        RatioEstimate? pairedRatio = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(thresholds);

        var samples = rawSamples ?? [];
        var violations = new List<string>();
        var notes = new List<string>();

        if (result.Errored)
            violations.Add($"Benchmark errored: {result.ErrorMessage}");

        violations.AddRange(BenchmarkAssert.Validate(result, new PerformanceThresholds
        {
            MaxMeanNs = thresholds.MaxMeanNs >= 0 ? thresholds.MaxMeanNs : null,
            MaxP95Ns = thresholds.MaxP95Ns >= 0 ? thresholds.MaxP95Ns : null,
            MaxAllocatedBytes = thresholds.MaxAllocatedBytes >= 0 ? thresholds.MaxAllocatedBytes : null,
            MaxAbsoluteThresholdTolerance = thresholds.MaxAbsoluteThresholdTolerance,
        }));

        if (thresholds.RequireIsolation && !result.IsolationStatus.IsIsolated())
        {
            if (allowInProcessGate)
            {
                // Waived, and said so. A gate that quietly declines to enforce something is a gate that
                // passes, and the note is the only thing standing between that and a reader believing
                // the number was measured somewhere NBenchmark chose.
                notes.Add(
                    $"'{result.Name}' was measured in the test host ({result.IsolationStatus.ToLabel()}); "
                    + "the isolation requirement is waived because [AllowInProcessGate] is present. The "
                    + "host's JIT tiering, PGO and GC flavour are whatever the preceding tests left "
                    + "behind, so treat the absolute numbers as indicative.");
            }
            else
            {
                var remedy = result.IsolationStatus.ToRemedy();

                violations.Add(
                    $"'{result.Name}' was measured in the test host ({result.IsolationStatus.ToLabel()}), "
                    + "so the number does not describe a runtime configuration NBenchmark chose. "
                    + "Performance gates require isolation by default."
                    + (remedy is null ? "" : $" To isolate it: {remedy}.")
                    + " To gate on a host measurement anyway, add [AllowInProcessGate] to the test "
                    + "method, its class, or its assembly.");
            }
        }

        if (thresholds.MaxSlowdownRatio <= 0 || result.Errored)
            return new Outcome(violations, notes);

        if (referenceResult is not null && referenceSamples is not null)
        {
            if (RatioIsEnforceable(result, referenceResult, allowInProcessGate, notes))
            {
                var verdict = RelativeComparison.CheckStructured(
                    result, samples, referenceResult, referenceSamples, thresholds.MaxSlowdownRatio,
                    pairedRatio: pairedRatio);

                violations.AddRange(verdict.Violations);
                NoteRatioEvidence(verdict, thresholds.MaxSlowdownRatio, result.Name, notes);
            }

            return new Outcome(violations, notes);
        }

        // No reference method: the ratio is against the calibration standard, which measures this
        // machine's speed rather than a competing implementation.
        //
        // Which calibration matters. The divisor has to have been measured under the same runtime
        // configuration as the candidate, or the ratio reports the difference between two process
        // configurations - worth ~3.3x on bodies of identical cost - rather than anything about the
        // code. So an isolated result is divided by the calibration its own worker measured, and a
        // host-measured result by the host's.
        var calibration = workerCalibration ?? PerformanceCalibration.Run();

        var calibrationStatus = workerCalibration is not null
            ? IsolationStatus.Isolated
            : IsolationStatus.InProcessRequested;

        // The calibration is measured once per replicate worker, after that worker's own benchmark
        // work, so its per-launch medians pair with the benchmark's the same way a reference method's
        // do. Empty for a single-launch run, and Estimate then returns null.
        var calibrationVerdict = RelativeComparison.CheckStructured(
            result,
            samples,
            CalibrationStandard.ToBenchmarkResult(calibration, calibrationStatus),
            calibration.Samples,
            thresholds.MaxSlowdownRatio,
            pairedRatio: LogRatio.Estimate(result, calibration.LaunchMedians));

        violations.AddRange(calibrationVerdict.Violations);
        NoteRatioEvidence(calibrationVerdict, thresholds.MaxSlowdownRatio, result.Name, notes);

        // Only the mismatched case needs saying. When both were measured the same way the ratio is
        // sound, and a note on every passing test is noise that trains people to skip the notes.
        if (result.IsolationStatus.IsIsolated() && workerCalibration is null)
        {
            notes.Add(
                $"NBenchmark: '{result.Name}' was measured in a worker process but its calibration was "
                + "measured in the test host, so the ratio spans two runtime configurations. Treat it as a "
                + "rough hardware-scaled bound rather than a code comparison. This usually means the worker "
                + "could not measure the calibration; its stderr will say why.");
        }

        return new Outcome(violations, notes);
    }

    /// <summary>
    ///     Says what evidence the ratio verdict rests on, in the two cases where the verdict alone
    ///     misleads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>A pass that only just passed.</b> With replicates, a ratio past the gate whose interval
    ///         still spans <c>1.00x</c> does not fail - the run cannot distinguish the two bodies at all,
    ///         and failing a build on it would be failing on noise. But silence there is indistinguishable
    ///         from a comfortable pass, so the note says the gate was not enforced and why.
    ///     </para>
    ///     <para>
    ///         <b>A failure with no interval.</b> Without replicates the ratio is one quotient of two
    ///         numbers, and nothing about it says whether a re-run would produce the same verdict. Said
    ///         only on failure, where someone is already reading the output and about to act on it.
    ///     </para>
    /// </remarks>
    private static void NoteRatioEvidence(
        RelativeComparisonVerdict verdict,
        double maxSlowdownRatio,
        string name,
        List<string> notes)
    {
        if (double.IsNaN(verdict.Ratio))
            return;

        if (verdict.Estimate is { } estimate)
        {
            if (!verdict.IsRegression && estimate.Value > maxSlowdownRatio && estimate.IncludesUnity)
            {
                notes.Add(
                    $"NBenchmark: the ratio gate for '{name}' was not enforced - the paired ratio is "
                    + $"{estimate.Value:F2}x, past the {maxSlowdownRatio:F2}x gate, but its "
                    + $"{estimate.ConfidenceLevel:P0} interval [{estimate.Lower:F2}-{estimate.Upper:F2}x] over "
                    + $"{estimate.Replicates} replicates spans 1.00x, so this run cannot distinguish the two "
                    + "bodies. Raise LaunchCount to narrow it.");
            }

            return;
        }

        if (verdict.IsRegression)
        {
            notes.Add(
                $"NBenchmark: the ratio for '{name}' is a point estimate with no interval, because the test "
                + "was measured in a single launch. It says nothing about whether a re-run would agree. Set "
                + "LaunchCount = 3 on the attribute to gate on a paired ratio with a confidence interval "
                + "instead.");
        }
    }

    /// <summary>
    ///     Whether a candidate/reference ratio may be gated on, appending the reason when it may not.
    /// </summary>
    private static bool RatioIsEnforceable(
        BenchmarkResult candidate,
        BenchmarkResult reference,
        bool allowInProcessGate,
        List<string> notes)
    {
        var candidateIsolated = candidate.IsolationStatus.IsIsolated();
        var referenceIsolated = reference.IsolationStatus.IsIsolated();

        if (candidateIsolated && referenceIsolated)
            return true;

        if (candidateIsolated != referenceIsolated)
        {
            var host = candidateIsolated ? reference.Name : candidate.Name;

            notes.Add(
                $"NBenchmark: the ratio gate for '{candidate.Name}' was not enforced - it and its reference "
                + $"were measured in different processes ('{host}' ran in the test host), so their ratio "
                + "would describe the two processes rather than the two bodies. [AllowInProcessGate] does "
                + "not cover this case; make both sides isolatable instead.");

            return false;
        }

        if (allowInProcessGate)
        {
            notes.Add(
                $"NBenchmark: the ratio gate for '{candidate.Name}' was enforced on two test-host "
                + "measurements because [AllowInProcessGate] is present. The host's JIT state is shared "
                + "with every preceding test, so treat a marginal result as inconclusive.");

            return true;
        }

        notes.Add(
            $"NBenchmark: the ratio gate for '{candidate.Name}' was not enforced - both it and its reference "
            + "were measured in the test host, where the runtime configuration is whatever the preceding "
            + "tests left behind. On bodies of provably identical cost that produced a 2.80x ratio with a "
            + "tight interval. Make the test isolatable, or add [AllowInProcessGate] to gate on it anyway.");

        return false;
    }
}
