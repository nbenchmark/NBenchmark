namespace NBenchmark;

/// <summary>
///     Evidence-based interference rejection: before the statistical outlier detector ever sees the
///     sample stream, discard samples the OS is known to have preempted - measured via the
///     measuring thread's own CPU-occupancy ratio, not inferred from the timing value.
/// </summary>
/// <remarks>
///     <para>
///         Every timed sample is bracketed with a thread-CPU-time read
///         (<see cref="NBenchmark.Interop.ThreadCpuClock" />) immediately outside the timed window,
///         giving a per-sample occupancy ratio <c>r_i = cpuDelta_i / wallDelta_i</c>. A sample whose
///         ratio falls materially below this benchmark's own median is rejected before
///         <c>OutlierTrim</c> runs - a fact about that sample, not a guess drawn from its timing.
///     </para>
///     <para>
///         On by default, like <see cref="DriftCanaryOptions" /> and the jitter probe: the guard in
///         <see cref="ProbeCostBudgetFraction" /> makes it safe to default on even on a platform
///         (macOS) whose probe cost this repository does not advertise a number for. It degrades
///         gracefully and reports why whenever it cannot safely operate - an unsupported platform, a
///         probe too expensive relative to the sample duration, or too few samples with a known
///         occupancy reading (typically an async body whose continuations mostly hopped threads) -
///         rather than silently doing nothing.
///     </para>
/// </remarks>
public sealed record InterferenceOptions
{
    /// <summary>The smallest legal <see cref="RejectionThreshold" />.</summary>
    internal const double MinRejectionThreshold = 0.01;

    /// <summary>The largest legal <see cref="RejectionThreshold" />.</summary>
    internal const double MaxRejectionThreshold = 1.0;

    /// <summary>
    ///     The default <see cref="RejectionThreshold" />: 0.5. A sample whose CPU occupancy falls
    ///     below half this benchmark's own median occupancy is treated as preempted.
    /// </summary>
    internal const double DefaultRejectionThreshold = 0.5;

    /// <summary>The smallest legal <see cref="ProbeCostBudgetFraction" />.</summary>
    internal const double MinProbeCostBudgetFraction = 0.0001;

    /// <summary>The largest legal <see cref="ProbeCostBudgetFraction" />.</summary>
    internal const double MaxProbeCostBudgetFraction = 1.0;

    /// <summary>
    ///     The default <see cref="ProbeCostBudgetFraction" />: 0.05 (5%). Below this, bracketing a
    ///     sample with two thread-CPU-clock reads costs an amount of wall time small enough next to
    ///     the sample it is measuring to be a rounding error rather than a confound.
    /// </summary>
    internal const double DefaultProbeCostBudgetFraction = 0.05;

    /// <summary>The smallest legal <see cref="KnownSampleFraction" />.</summary>
    internal const double MinKnownSampleFraction = 0.0;

    /// <summary>The largest legal <see cref="KnownSampleFraction" />.</summary>
    internal const double MaxKnownSampleFraction = 1.0;

    /// <summary>
    ///     The default <see cref="KnownSampleFraction" />: 0.5. At least half the stream must have a
    ///     known occupancy reading before a median is trusted enough to reject against.
    /// </summary>
    internal const double DefaultKnownSampleFraction = 0.5;

    /// <summary>The smallest legal <see cref="HighRejectionWarningFraction" />.</summary>
    internal const double MinHighRejectionWarningFraction = 0.0;

    /// <summary>The largest legal <see cref="HighRejectionWarningFraction" />.</summary>
    internal const double MaxHighRejectionWarningFraction = 1.0;

    /// <summary>
    ///     The default <see cref="HighRejectionWarningFraction" />: 0.2 (20%). Past this, the host is
    ///     noisy enough that the survivors are a small, possibly biased slice of what was measured.
    /// </summary>
    internal const double DefaultHighRejectionWarningFraction = 0.2;

    public static readonly InterferenceOptions Default = new();

    /// <summary>The filter switched off. No samples are ever rejected on evidence.</summary>
    public static readonly InterferenceOptions Disabled = new() { Enabled = false };

    private readonly double _highRejectionWarningFraction = DefaultHighRejectionWarningFraction;
    private readonly double _knownSampleFraction = DefaultKnownSampleFraction;
    private readonly double _probeCostBudgetFraction = DefaultProbeCostBudgetFraction;
    private readonly double _rejectionThreshold = DefaultRejectionThreshold;

    /// <summary>
    ///     Whether the filter runs at all. On by default; set to <c>false</c> (CLI:
    ///     <c>--no-interference-filter</c>) to trim only on the statistical detector, as before this
    ///     feature existed.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     A sample is rejected when its CPU-occupancy ratio falls below this fraction of the
    ///     benchmark's own median ratio. Must be between <see cref="MinRejectionThreshold" /> and
    ///     <see cref="MaxRejectionThreshold" />. Default <see cref="DefaultRejectionThreshold" />
    ///     (0.5): a sample that held the CPU for less than half of what a typical sample in this run
    ///     did was, as a matter of fact, preempted for some of its window.
    /// </summary>
    public double RejectionThreshold
    {
        get => _rejectionThreshold;
        init => _rejectionThreshold = value is >= MinRejectionThreshold and <= MaxRejectionThreshold
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"InterferenceOptions.RejectionThreshold must be between {MinRejectionThreshold} "
                + $"and {MaxRejectionThreshold}.");
    }

    /// <summary>
    ///     The probe is disabled for a run when two thread-CPU-clock reads cost more than this
    ///     fraction of the resolved sample-duration target - the guard that makes the feature safe to
    ///     default on despite the macOS probe cost not being known until measured. Must be between
    ///     <see cref="MinProbeCostBudgetFraction" /> and <see cref="MaxProbeCostBudgetFraction" />.
    ///     Default <see cref="DefaultProbeCostBudgetFraction" /> (5%).
    /// </summary>
    public double ProbeCostBudgetFraction
    {
        get => _probeCostBudgetFraction;
        init => _probeCostBudgetFraction =
            value is >= MinProbeCostBudgetFraction and <= MaxProbeCostBudgetFraction
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"InterferenceOptions.ProbeCostBudgetFraction must be between "
                    + $"{MinProbeCostBudgetFraction} and {MaxProbeCostBudgetFraction}.");
    }

    /// <summary>
    ///     The minimum fraction of measured samples that must carry a known occupancy reading before
    ///     the filter trusts a median enough to reject against. Below this - typically an async body
    ///     whose continuations mostly resumed on a different thread - the filter disables itself for
    ///     the benchmark and reports why, rather than rejecting from a handful of readings. Must be
    ///     between <see cref="MinKnownSampleFraction" /> and <see cref="MaxKnownSampleFraction" />.
    ///     Default <see cref="DefaultKnownSampleFraction" /> (0.5).
    /// </summary>
    public double KnownSampleFraction
    {
        get => _knownSampleFraction;
        init => _knownSampleFraction = value is >= MinKnownSampleFraction and <= MaxKnownSampleFraction
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value,
                $"InterferenceOptions.KnownSampleFraction must be between {MinKnownSampleFraction} "
                + $"and {MaxKnownSampleFraction}.");
    }

    /// <summary>
    ///     A warning fires when the fraction of samples rejected as preempted exceeds this value -
    ///     the "this host is too noisy to trust" signal. Must be between
    ///     <see cref="MinHighRejectionWarningFraction" /> and
    ///     <see cref="MaxHighRejectionWarningFraction" />. Default
    ///     <see cref="DefaultHighRejectionWarningFraction" /> (20%).
    /// </summary>
    public double HighRejectionWarningFraction
    {
        get => _highRejectionWarningFraction;
        init => _highRejectionWarningFraction =
            value is >= MinHighRejectionWarningFraction and <= MaxHighRejectionWarningFraction
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), value,
                    $"InterferenceOptions.HighRejectionWarningFraction must be between "
                    + $"{MinHighRejectionWarningFraction} and {MaxHighRejectionWarningFraction}.");
    }
}
