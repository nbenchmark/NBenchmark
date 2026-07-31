namespace NBenchmark.Integration.Abstractions;

/// <summary>
///     The test host's own measurement of <see cref="CalibrationStandard" />, cached for the life of
///     the process.
/// </summary>
/// <remarks>
///     <para>
///         Caching is what makes this cheap enough to consult on every gated test, and it is safe
///         because the standard measures the machine rather than anything the tests do.
///     </para>
///     <para>
///         This is the <i>fallback</i> calibration. A test measured in a worker gets one measured in
///         that same worker instead, so both sides of its ratio share a runtime configuration - see
///         <see cref="PerformanceGate.Evaluate" />. This one is used when the benchmark also ran in
///         the host, where it is the correct comparison rather than a compromise.
///     </para>
/// </remarks>
public static class PerformanceCalibration
{
    private static readonly Lazy<CalibrationResult> Cached = new(CalibrationStandard.Measure);

    public static CalibrationResult Run() => Cached.Value;

    /// <summary>
    ///     The host calibration as a comparable result, labelled as host-measured so a gate can tell
    ///     it apart from one a worker produced.
    /// </summary>
    public static BenchmarkResult CreateBenchmarkResult()
        => CalibrationStandard.ToBenchmarkResult(Run(), IsolationStatus.InProcessRequested);
}
