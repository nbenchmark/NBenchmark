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
