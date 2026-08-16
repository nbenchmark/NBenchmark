namespace NBenchmark;

/// <summary>
///     Settings for the host drift canary: a deterministic control workload measured at each
///     benchmark boundary, so a run can say how much the host's effective speed moved while it
///     was running.
/// </summary>
/// <remarks>
///     <para>
///         Every drift check inside the measurement engine operates on one benchmark's own sample
///         stream. A thermal ramp, or a background process starting halfway through, moves every
///         benchmark measured after it and none measured before - which confounds every
///         cross-benchmark comparison in the run without disturbing any single benchmark's
///         internal statistics. The canary is the measurement that makes that visible: the same
///         fixed work, run at each boundary, so a change in how long it takes is a change in the
///         machine.
///     </para>
///     <para>
///         On by default, like the jitter probe it shares a workload with. A reading at the
///         defaults is a fraction of a millisecond, so a twenty-benchmark run pays for it in
///         microseconds; the readings are taken between benchmarks, never inside a timed window,
///         so the feature can cost wall-clock but never accuracy.
///     </para>
/// </remarks>
public sealed record DriftCanaryOptions
{
    /// <summary>The smallest legal <see cref="Samples" />. Below four there is no usable median.</summary>
    public const int MinSamples = 4;

    /// <summary>The largest legal <see cref="Samples" />.</summary>
    public const int MaxSamples = 1024;

    /// <summary>
    ///     The default <see cref="Samples" />: 32, matching
    ///     <see cref="AutoTuneOptions.JitterCalibrationSamples" /> so the canary and the pre-flight
    ///     jitter probe read the same workload the same way.
    /// </summary>
    public const int DefaultSamples = 32;

    /// <summary>The smallest legal <see cref="WorkPerSample" />.</summary>
    public const int MinWorkPerSample = 64;

    /// <summary>The largest legal <see cref="WorkPerSample" />.</summary>
    public const int MaxWorkPerSample = 1 << 20;

    /// <summary>
    ///     The default <see cref="WorkPerSample" />: 4,096, matching
    ///     <see cref="AutoTuneOptions.JitterCalibrationWorkPerSample" />.
    /// </summary>
    public const int DefaultWorkPerSample = 4096;

    /// <summary>The smallest legal <see cref="MinimumReportableDrift" />.</summary>
    public const double MinReportableDrift = 0.0;

    /// <summary>The largest legal <see cref="MinimumReportableDrift" />.</summary>
    public const double MaxReportableDrift = 1.0;

    /// <summary>
    ///     The default <see cref="MinimumReportableDrift" />: 0.01 (1%). Below that the canary is
    ///     measuring its own noise rather than the host's speed, and a warning drawn from it would
    ///     fire on every quiet run.
    /// </summary>
    public const double DefaultMinimumReportableDrift = 0.01;

    /// <summary>The defaults: on, 32 samples of 4,096 iterations, warning past 1% drift.</summary>
    public static readonly DriftCanaryOptions Default = new();

    /// <summary>The canary switched off. No readings are taken and no stamp is attached.</summary>
    public static readonly DriftCanaryOptions Disabled = new() { Enabled = false };

    private readonly double _minimumReportableDrift = DefaultMinimumReportableDrift;
    private readonly int _samples = DefaultSamples;
    private readonly int _workPerSample = DefaultWorkPerSample;

    /// <summary>
    ///     Whether the canary runs. On by default; set to <c>false</c> (CLI:
    ///     <c>--no-drift-canary</c>) to take no readings at all, which leaves
    ///     <see cref="BenchmarkResult.HostTimeline" /> <c>null</c> on every result and silences
    ///     the drift warning.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     How many timed samples one canary reading collects. Must be between
    ///     <see cref="MinSamples" /> and <see cref="MaxSamples" />. Default
    ///     <see cref="DefaultSamples" />.
    /// </summary>
    public int Samples
    {
        get => _samples;
        init => _samples = value is >= MinSamples and <= MaxSamples
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"DriftCanaryOptions.Samples must be between {MinSamples} and {MaxSamples}.");
    }

    /// <summary>
    ///     How many busy-weight iterations each canary sample performs. Must be between
    ///     <see cref="MinWorkPerSample" /> and <see cref="MaxWorkPerSample" />. Default
    ///     <see cref="DefaultWorkPerSample" />.
    /// </summary>
    public int WorkPerSample
    {
        get => _workPerSample;
        init => _workPerSample = value is >= MinWorkPerSample and <= MaxWorkPerSample
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"DriftCanaryOptions.WorkPerSample must be between {MinWorkPerSample} and {MaxWorkPerSample}.");
    }

    /// <summary>
    ///     How far the host's effective speed must move between two benchmarks' measurement points
    ///     before the drift is worth mentioning, as a fraction. Must be between
    ///     <see cref="MinReportableDrift" /> and <see cref="MaxReportableDrift" />. Default
    ///     <see cref="DefaultMinimumReportableDrift" /> (1%); <c>0</c> reports any drift at all.
    /// </summary>
    public double MinimumReportableDrift
    {
        get => _minimumReportableDrift;
        init => _minimumReportableDrift =
            double.IsFinite(value) && value is >= MinReportableDrift and <= MaxReportableDrift
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"DriftCanaryOptions.MinimumReportableDrift must be between {MinReportableDrift} "
                    + $"and {MaxReportableDrift}.");
    }
}
