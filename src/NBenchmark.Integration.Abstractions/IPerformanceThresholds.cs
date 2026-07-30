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
    ///     process. Defaults to <c>false</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A gate is a claim about the code. A number measured in the test host is partly a claim
    ///         about the host: JIT tiering, dynamic PGO and GC flavour are fixed when a process
    ///         starts, and the test host's are whatever the preceding tests left behind. NBenchmark
    ///         isolates what it can and labels what it cannot, but labelling is a message to a human
    ///         reading output, and CI does not read output.
    ///     </para>
    ///     <para>
    ///         Setting this turns "this was measured somewhere I did not choose" into a test failure,
    ///         so a benchmark that silently stops being isolatable - someone adds a fixture argument,
    ///         or the worker fails to deploy - is caught rather than quietly measured elsewhere.
    ///         <see cref="BenchmarkResult.IsolationStatus" /> names the reason in the failure message.
    ///     </para>
    /// </remarks>
    public bool RequireIsolation => false;
}
