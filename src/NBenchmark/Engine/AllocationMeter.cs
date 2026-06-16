namespace NBenchmark.Engine;

/// <summary>
///     Per-sample allocation measurement shared by the runner and the adaptive measurement loop.
///     Prefers the per-thread allocation counter and falls back to the process-wide counter when a
///     sample resumes on a different thread (e.g. an async continuation).
/// </summary>
internal static class AllocationMeter
{
    public static AllocationSnapshot Capture()
        => new(
            GC.GetAllocatedBytesForCurrentThread(),
            GC.GetTotalAllocatedBytes(),
            Environment.CurrentManagedThreadId);

    public static long Delta(AllocationSnapshot before)
    {
        if (Environment.CurrentManagedThreadId == before.ThreadId)
            return Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - before.ThreadBytes);

        // Async continuations may resume on a different worker thread.
        // Fall back to the process-wide allocation delta in that case.
        return Math.Max(0, GC.GetTotalAllocatedBytes() - before.ProcessBytes);
    }

    public readonly record struct AllocationSnapshot(long ThreadBytes, long ProcessBytes, int ThreadId);
}
