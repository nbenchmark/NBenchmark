using System.Runtime.CompilerServices;

namespace NBenchmark.Engine;

/// <summary>Garbage-collection control shared by the runner and the adaptive measurement loop.</summary>
internal static class GcControl
{
    /// <summary>Forces a blocking Gen0 collection. Used before each measured sample under the Independent profile.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ForceGen0Collection() => GC.Collect(0, GCCollectionMode.Forced, true);

    /// <summary>Forces a blocking full collection and drains finalizers. Used between benchmarks.</summary>
    public static void ForceFullGc()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
    }
}
