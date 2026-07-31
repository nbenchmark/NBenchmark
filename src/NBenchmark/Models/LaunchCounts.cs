namespace NBenchmark;

/// <summary>
///     The bounds and defaults for the <b>replicate count</b> - how many separate processes measure a
///     benchmark - together in one place.
/// </summary>
/// <remarks>
///     <para>
///         Deliberately <em>not</em> a field on <see cref="MeasurementOptions" />. A replicate is a
///         worker, so the count is spent by whichever coordinator launches them - the harness, an
///         inline suite, a <c>[BenchmarkPlan]</c> run, or a test-framework gate - and a worker has no
///         use for it at all: it measures exactly once, and a worker that repeated the measurement
///         internally would report within-process precision as though it were reproducibility.
///     </para>
///     <para>
///         <see cref="MeasurementOptions" /> is serialized whole and handed to every worker, so any
///         replicate count living on it would travel to a process that must ignore it. That shape had
///         already produced one accident nobody chose: every request path pinned the field to 1 for
///         the wire, and the pin then silently decided whether a
///         <c>[Benchmark(LaunchCount = n)]</c> attribute override applied. The count is passed
///         explicitly instead, so there is no field for a worker to be handed and no interaction to
///         reason about.
///     </para>
/// </remarks>
public static class LaunchCounts
{
    /// <summary>One launch: measure once, in one process. No between-process estimate.</summary>
    public const int Single = 1;

    /// <summary>
    ///     The ceiling on a requested launch count, for every path that accepts one - the
    ///     <c>--launch-count</c> flag, <c>WithLaunchCount</c>, <c>[Benchmark(LaunchCount = ...)]</c>
    ///     and the test attributes' <c>LaunchCount</c>.
    /// </summary>
    public const int Max = 100;

    /// <summary>
    ///     What Harness mode launches when the caller pinned nothing.
    /// </summary>
    /// <remarks>
    ///     Harness mode defaults above one so the launch-aggregation view - the honest account of
    ///     run-to-run variance from process-level effects (ASLR, scheduler placement, tiered JIT) -
    ///     is surfaced without users having to ask for it. <see cref="Benchmark.Run" /> and
    ///     <see cref="BenchmarkSuite" /> stay at <see cref="Single" /> unless the caller raises it,
    ///     because neither reports a cross-launch interval by default.
    /// </remarks>
    public const int HarnessDefault = 3;

    /// <summary>Whether <paramref name="count" /> is a launch count this library will accept.</summary>
    public static bool IsValid(int count) => count is >= Single and <= Max;

    /// <summary>
    ///     Brings <paramref name="count" /> into range rather than rejecting it.
    /// </summary>
    /// <remarks>
    ///     For the attribute paths, where the value is a compile-time constant on a test method and
    ///     throwing would fail the test with a configuration error instead of measuring it. Paths that
    ///     can report the mistake to the caller - the fluent builders and the CLI parser - validate
    ///     against <see cref="IsValid" /> and say so instead.
    /// </remarks>
    public static int Clamp(int count) => Math.Clamp(count, Single, Max);
}
