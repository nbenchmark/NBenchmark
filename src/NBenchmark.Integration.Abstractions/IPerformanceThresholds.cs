namespace NBenchmark.Integration.Abstractions;

public interface IPerformanceThresholds
{
    public double MaxMeanNs { get; }
    public double MaxP95Ns { get; }
    public long MaxAllocatedBytes { get; }
    public string? ReferenceMethod { get; }
    public double MaxSlowdownRatio { get; }
    public int Iterations { get; }
    public int WarmupIterations { get; }
    public bool MeasureAllocations { get; }
    public OutlierMode OutlierMode { get; }
    public double ConfidenceLevel { get; }
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
