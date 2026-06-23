using System.Diagnostics;

namespace NBenchmark.Engine;

internal static class DiagnosticMeter
{
    public readonly record struct Snapshot(int Gen0, int Gen1, int Gen2, long CpuTimeTicks);

    private static readonly Process CurrentProcess = Process.GetCurrentProcess();

    public static Snapshot Capture(DiagnosticsOptions opts)
    {
        var gen0 = opts.GcCollectionCounts ? GC.CollectionCount(0) : 0;
        var gen1 = opts.GcCollectionCounts ? GC.CollectionCount(1) : 0;
        var gen2 = opts.GcCollectionCounts ? GC.CollectionCount(2) : 0;
        var cpuTicks = opts.CpuTime ? CurrentProcess.TotalProcessorTime.Ticks : 0L;
        return new Snapshot(gen0, gen1, gen2, cpuTicks);
    }

    public static DiagnosticDelta Delta(Snapshot before, DiagnosticsOptions opts)
    {
        var gen0 = opts.GcCollectionCounts ? Math.Max(0, GC.CollectionCount(0) - before.Gen0) : 0;
        var gen1 = opts.GcCollectionCounts ? Math.Max(0, GC.CollectionCount(1) - before.Gen1) : 0;
        var gen2 = opts.GcCollectionCounts ? Math.Max(0, GC.CollectionCount(2) - before.Gen2) : 0;
        var cpuTicks = opts.CpuTime
            ? Math.Max(0, CurrentProcess.TotalProcessorTime.Ticks - before.CpuTimeTicks)
            : 0L;
        return new DiagnosticDelta(gen0, gen1, gen2, cpuTicks);
    }
}
