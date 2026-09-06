namespace NBenchmark.Integration.Abstractions;

public interface IPerformanceThresholds
{
    /// <summary>
    ///     The value every optional numeric threshold carries when it was not set.
    ///     <para>
    ///         The thresholds live on attributes, and an attribute argument cannot be a
    ///         <see cref="Nullable{T}" /> - so "unset" has to be a value in range rather than the
    ///         absence of one. Every threshold on this interface uses this one sentinel, and every
    ///         gate reads it as "do not check this": a negative maximum duration, byte count or
    ///         slowdown ratio has no other meaning.
    ///     </para>
    /// </summary>
    public const double Unset = -1;

    /// <summary>The <see cref="Unset" /> sentinel for <see cref="MaxAllocatedBytes" />.</summary>
    public const long UnsetBytes = -1;

    /// <summary>
    ///     The value <see cref="Samples" /> and <see cref="WarmupSamples" /> carry when they were not
    ///     set: the engine chooses the count adaptively. Zero rather than <see cref="Unset" /> because
    ///     a count is a natural number - "as many as it takes" is zero explicit samples, not minus one.
    /// </summary>
    public const int AutoSampleCount = 0;

    /// <summary>Maximum mean time per operation in nanoseconds, or <see cref="Unset" />.</summary>
    public double MaxMeanNs { get; }

    /// <summary>
    ///     Maximum median time per operation in nanoseconds, or <see cref="Unset" />.
    ///     <para>
    ///         The median is the statistic the reports lead with and the one significance is decided
    ///         on, so it is the one most absolute limits mean. Prefer it over <see cref="MaxMeanNs" />
    ///         unless the average is genuinely what is being bounded.
    ///     </para>
    /// </summary>
    public double MaxMedianNs { get; }

    /// <summary>Maximum 95th-percentile time per operation in nanoseconds, or <see cref="Unset" />.</summary>
    public double MaxP95Ns { get; }

    /// <summary>Maximum mean bytes allocated per operation, or <see cref="UnsetBytes" />.</summary>
    public long MaxAllocatedBytes { get; }

    /// <summary>
    ///     The name of the method to measure alongside this one as the denominator of
    ///     <see cref="MaxSlowdownRatio" />, or <c>null</c> when there is no comparison.
    /// </summary>
    public string? ReferenceMethod { get; }

    /// <summary>
    ///     Maximum ratio of this measurement to <see cref="ReferenceMethod" />'s, or
    ///     <see cref="Unset" />. Reads as "no more than N times slower than the reference".
    /// </summary>
    public double MaxSlowdownRatio { get; }

    /// <summary>Measured samples to take, or <see cref="AutoSampleCount" />.</summary>
    public int Samples { get; }

    /// <summary>Warmup samples to take before measuring, or <see cref="AutoSampleCount" />.</summary>
    public int WarmupSamples { get; }

    /// <summary>Whether to measure allocations as well as time.</summary>
    public bool MeasureAllocations { get; }

    /// <summary>Which samples to trim before the statistics are computed.</summary>
    public OutlierMode OutlierMode { get; }

    /// <summary>The confidence level for the reported interval, e.g. <c>0.95</c>.</summary>
    public double ConfidenceLevel { get; }

    /// <summary>
    ///     How far past an absolute threshold a measurement may land before the gate fails, as a
    ///     multiplier of the threshold. <c>1.0</c> fails at the threshold exactly.
    /// </summary>
    public double MaxAbsoluteThresholdTolerance { get; }

    /// <summary>
    ///     How many worker processes to measure this test in. Defaults to <c>1</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Each replicate is a <b>fresh worker</b>, and both sides of a
    ///         <see cref="MaxSlowdownRatio" /> comparison are measured together inside each one. That is
    ///         what a replicate buys: with two or more, the ratio becomes a paired per-replicate estimate
    ///         with a confidence interval, so a failure means the slowdown is larger than the difference
    ///         between two runs of the same code. At <c>1</c> the ratio is a single quotient with no
    ///         interval, and nothing in the result says whether a re-run would agree.
    ///     </para>
    ///     <para>
    ///         It is off by default because it is not free: <c>LaunchCount = 3</c> spends three worker
    ///         launches on this test instead of one. That is a trade every suite should not be made to
    ///         pay, so raise it on the comparisons that decide a build rather than across the board. Two
    ///         is the smallest value that produces an interval; three is enough for the interval to be
    ///         worth reading.
    ///     </para>
    ///     <para>
    ///         Read only by the attribute pattern, which owns the measurement. In the
    ///         <c>PerformanceAssert</c> pattern the caller has already measured, so there is nothing left
    ///         for this to change.
    ///     </para>
    /// </remarks>
    public int LaunchCount => 1;

    /// <summary>
    ///     Fails the test when the measurement was taken in the test host rather than in a worker
    ///     process. Defaults to <c>true</c>; opt out with <c>[AllowInProcessGate]</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A gate is a claim about the code. A number measured in the test host is partly a claim
    ///         about the host: JIT tiering, dynamic PGO and GC flavour are fixed when a process
    ///         starts, and the test host's are whatever the preceding tests left behind. NBenchmark
    ///         isolates what it can and labels what it cannot, but labelling is a message to a human
    ///         reading output, and CI does not read output. So the default fails the test: a benchmark
    ///         that silently stops being isolatable - someone adds a fixture argument, or the worker
    ///         fails to deploy on a build agent - is caught rather than quietly measured elsewhere and
    ///         reported as a pass. <see cref="BenchmarkResult.IsolationStatus" /> names the reason.
    ///     </para>
    ///     <para>
    ///         The opt-out is <see cref="AllowInProcessGateAttribute" />, on the test method, its class
    ///         or its assembly - not a <c>false</c> here. Two reasons. It already means "this test
    ///         cannot be isolated and I accept a host measurement", so a second knob saying the same
    ///         thing would be one more place for the two to disagree. And <c>false</c> could not be
    ///         expressed reliably anyway: xUnit reads attribute values as named arguments, where an
    ///         absent argument and an explicit <c>false</c> are the same thing, and attribute arguments
    ///         cannot be <see cref="Nullable{T}" /> to tell them apart. A setting that is silently
    ///         ignored on one framework is worse than no setting.
    ///     </para>
    ///     <para>
    ///         Implemented rather than inherited only by the <c>PerformanceAssert</c> option bags, which
    ///         are ordinary objects with no attribute target to carry <c>[AllowInProcessGate]</c>. The
    ///         attribute pattern leaves this to the default.
    ///     </para>
    /// </remarks>
    public bool RequireIsolation => true;
}
