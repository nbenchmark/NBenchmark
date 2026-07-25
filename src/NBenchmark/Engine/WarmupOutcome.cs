using System.Runtime;

namespace NBenchmark.Engine;

/// <summary>
///     What the warmup phase observed, bundled so the sync and async adaptive loops can hand it to
///     result construction as one value instead of a growing tail of parameters.
/// </summary>
/// <param name="TimeFloorMet">
///     Whether warmup reached <see cref="AutoTuneOptions.MinWarmupTime" />. When it did not, the body
///     may still have been running pre-tier-1 code when measurement started.
/// </param>
/// <param name="JitCompiledMethods">Methods the JIT compiled over warmup.</param>
/// <param name="JitCompilationTime">
///     Wall-clock time the JIT spent compiling over warmup. The honest answer to "what did tiering
///     cost here?" - unlike the method count, it is denominated in the same units as the benchmark.
/// </param>
/// <param name="JitCompiledIlBytes">IL bytes the JIT compiled over warmup.</param>
/// <param name="JitLastChangeAtNs">
///     How far into warmup the compiled-method count last moved. With the body under continuous load
///     this approximates when its hot path was promoted.
/// </param>
/// <param name="ElapsedNs">Total warmup elapsed nanoseconds.</param>
/// <param name="JitQuiescenceAchieved">Whether warmup ended with the JIT genuinely quiet.</param>
/// <param name="Curve">Per-op mean of each warmup batch, decimated to a bounded length.</param>
/// <param name="CurveSampleInterval">Warmup samples between consecutive <paramref name="Curve" /> points.</param>
internal readonly record struct WarmupOutcome(
    bool TimeFloorMet,
    long JitCompiledMethods,
    TimeSpan JitCompilationTime,
    long JitCompiledIlBytes,
    double JitLastChangeAtNs,
    double ElapsedNs,
    bool JitQuiescenceAchieved,
    double[] Curve,
    int CurveSampleInterval)
{
    /// <summary>
    ///     The outcome for a warmup phase that ran no plateau detection - pinned iteration counts and
    ///     calibration-capped warmup have no time floor to meet and collect no JIT signal.
    /// </summary>
    public static WarmupOutcome None(bool timeFloorMet) => new(
        timeFloorMet,
        JitCompiledMethods: 0,
        JitCompilationTime: TimeSpan.Zero,
        JitCompiledIlBytes: 0,
        JitLastChangeAtNs: 0,
        ElapsedNs: 0,
        JitQuiescenceAchieved: true,
        Curve: [],
        CurveSampleInterval: 0);
}

/// <summary>
///     A snapshot of the process-wide JIT counters, used to derive per-warmup deltas.
///     <para>
///         Every counter comes from <see cref="JitInfo" />, which is process-wide rather than
///         per-benchmark. In an in-process run several benchmarks share one process, so the first
///         benchmark to execute absorbs the bulk of startup compilation and later ones see almost
///         none. That is a real effect worth surfacing, not an artefact - but it does mean these
///         numbers describe the process during this benchmark's warmup, not the benchmark alone.
///     </para>
///     <para>
///         Reads are confined to warmup batch boundaries. The underlying QCalls can allocate and
///         trigger JIT activity themselves, so sampling them per-iteration would perturb the very
///         signal they report.
///     </para>
/// </summary>
internal readonly record struct JitCounters(long CompiledMethods, TimeSpan CompilationTime, long CompiledIlBytes)
{
    public static JitCounters Read() => new(
        JitInfo.GetCompiledMethodCount(),
        JitInfo.GetCompilationTime(),
        JitInfo.GetCompiledILBytes());

    /// <summary>This snapshot minus an earlier baseline.</summary>
    public JitCounters Since(in JitCounters baseline) => new(
        CompiledMethods - baseline.CompiledMethods,
        CompilationTime - baseline.CompilationTime,
        CompiledIlBytes - baseline.CompiledIlBytes);
}
