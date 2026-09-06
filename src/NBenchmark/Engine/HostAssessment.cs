namespace NBenchmark.Engine;

/// <summary>
///     What <see cref="EnvironmentControl.AssessHost" /> could learn about the host.
/// </summary>
/// <param name="CoreCount">Logical CPUs, as <see cref="Environment.ProcessorCount" /> reports them.</param>
/// <param name="IsMacOS"><c>true</c> when running on macOS.</param>
/// <param name="IsSharedRunner">
///     <c>true</c> when the host looks like a shared or otherwise unsuitable benchmark
///     environment. Read by <see cref="RegressionTolerance.NeedsRelaxation" /> and by the
///     test-integration gates, which relax a threshold rather than fail a build on a machine that
///     cannot hold a number still.
/// </param>
/// <param name="PerformanceCoreCount">
///     Performance ("P") cores, or <c>0</c> when the host does not report a core split. Zero means
///     <i>unknown</i>, not <i>none</i>.
/// </param>
/// <param name="EfficiencyCoreCount">
///     Efficiency ("E") cores, or <c>0</c>. Only meaningful alongside a non-zero
///     <paramref name="PerformanceCoreCount" />, since a homogeneous host has none.
/// </param>
/// <remarks>
///     The two core-split counts carry defaults so the three-argument shape keeps compiling: a
///     caller that only cares whether the host is a shared runner - which is most of them, and all
///     of the test-integration ones - should not have to describe its CPU topology to say so.
/// </remarks>
internal readonly record struct HostAssessment(
    int CoreCount,
    bool IsMacOS,
    bool IsSharedRunner,
    int PerformanceCoreCount = 0,
    int EfficiencyCoreCount = 0)
{
    /// <summary>
    ///     <c>true</c> when the host reports a performance/efficiency core split, so the two
    ///     counts describe the machine rather than standing in for "not known".
    /// </summary>
    public bool HasCoreSplit => PerformanceCoreCount > 0 && EfficiencyCoreCount > 0;
}
