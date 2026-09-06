namespace NBenchmark.Engine;

/// <summary>
///     Reusable jitter-tolerance relaxation logic shared between
///     <c>BenchmarkAssert</c> (the test-integration helper layer) and external
///     consumers such as NBenchmark.Studio. Extracted from
///     <c>NBenchmark.Integration.Abstractions</c> so Studio can reuse the same
///     relaxation rules without depending on the test-framework helper assembly.
/// </summary>
/// <remarks>
///     <para>
///         On a noisy host (a shared CI runner, or any host whose
///         <see cref="AutoTuneDiagnostic.JitterMetric" /> exceeds the
///         <see cref="AutoTuneOptions.JitterAutoSwitchThreshold" />), the threshold a
///         benchmark must not exceed is relaxed by a multiplier so genuine regressions
///         are still caught while host-noise false positives are suppressed. This helper
///         encapsulates the decision and the arithmetic so both
///         <c>BenchmarkAssert.Validate</c> and Studio apply identical tolerance.
///     </para>
///     <para>
///         The relaxation is applied to a single threshold value (a mean, a P95, or an
///         allocation byte count). The caller passes the raw measured value, the configured
///         threshold, and the relaxation multiplier; the helper returns the effective
///         threshold and a verdict saying whether the value exceeds it. Callers that want
///         to decide whether to relax at all (for example, only when the host is shared)
///         compute the multiplier upfront and pass <c>1.0</c> when no relaxation is
///         warranted - the helper itself is agnostic to the reason.
///     </para>
/// </remarks>
internal static class RegressionTolerance
{
    /// <summary>
    ///     Computes the effective threshold after applying the relaxation multiplier
    ///     and returns a structured verdict saying whether <paramref name="measuredValue" />
    ///     exceeds it. When <paramref name="toleranceMultiplier" /> is <c>1.0</c> (no
    ///     relaxation), the effective threshold equals <paramref name="configuredThreshold" />.
    /// </summary>
    /// <param name="measuredValue">The value the benchmark produced (mean ns, P95 ns, or allocated bytes).</param>
    /// <param name="configuredThreshold">The user-configured maximum the benchmark must not exceed.</param>
    /// <param name="toleranceMultiplier">
    ///     The relaxation multiplier (e.g. <c>2.0</c> to double the threshold). Pass
    ///     <c>1.0</c> when no relaxation is warranted. Must be &gt;= 1.0.
    /// </param>
    /// <returns>
    ///     A <see cref="ToleranceVerdict" /> with <see cref="ToleranceVerdict.EffectiveThreshold" />
    ///     (the configured threshold times the multiplier) and
    ///     <see cref="ToleranceVerdict.ExceedsThreshold" /> (<c>true</c> when
    ///     <paramref name="measuredValue" /> is above the effective threshold).
    /// </returns>
    public static ToleranceVerdict Evaluate(
        double measuredValue,
        double configuredThreshold,
        double toleranceMultiplier)
    {
        if (toleranceMultiplier < 1.0)
            throw new ArgumentOutOfRangeException(nameof(toleranceMultiplier), "Tolerance multiplier must be >= 1.0.");

        var effectiveThreshold = configuredThreshold * toleranceMultiplier;
        var exceeds = measuredValue > effectiveThreshold;

        return new ToleranceVerdict(
            measuredValue,
            configuredThreshold,
            toleranceMultiplier,
            effectiveThreshold,
            exceeds,
            exceeds ? measuredValue - effectiveThreshold : 0,
            toleranceMultiplier > 1.0);
    }

    /// <summary>
    ///     Decides whether threshold relaxation is warranted for the run that produced
    ///     <paramref name="result" />. Relaxation is applied when the host is a shared
    ///     runner (see <see cref="HostAssessment.IsSharedRunner" />) or when the run's
    ///     <see cref="AutoTuneDiagnostic.JitterMetric" /> exceeds
    ///     <paramref name="jitterAutoSwitchThreshold" /> (the same threshold at which the
    ///     adaptive loop switches its outlier detector).
    /// </summary>
    /// <param name="result">The benchmark result whose <see cref="BenchmarkResult.AutoTune" /> jitter metric is inspected.</param>
    /// <param name="isSharedRunner"><c>true</c> when the host looks like a shared CI runner.</param>
    /// <param name="jitterAutoSwitchThreshold">
    ///     The jitter metric value above which relaxation is applied. Pass
    ///     <c>AutoTuneOptions.Default.JitterAutoSwitchThreshold</c> to match the
    ///     engine's default, or a custom value.
    /// </param>
    /// <returns><c>true</c> when relaxation should be applied; otherwise <c>false</c>.</returns>
    public static bool NeedsRelaxation(
        BenchmarkResult result,
        bool isSharedRunner,
        double jitterAutoSwitchThreshold)
    {
        if (isSharedRunner)
            return true;

        var jitter = result.AutoTune?.JitterMetric;

        return jitter.HasValue && jitter.Value > jitterAutoSwitchThreshold;
    }
}

/// <summary>
///     The result of <see cref="RegressionTolerance.Evaluate" />: the measured value,
///     the configured and effective thresholds, whether the effective threshold was
///     exceeded, the excess (positive when exceeded), and whether the threshold was
///     relaxed (multiplier &gt; 1.0).
/// </summary>
internal sealed record ToleranceVerdict(
    double MeasuredValue,
    double ConfiguredThreshold,
    double ToleranceMultiplier,
    double EffectiveThreshold,
    bool ExceedsThreshold,
    double Excess,
    bool Relaxed);
