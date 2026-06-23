namespace NBenchmark.Engine;

internal readonly record struct HeapSnapshot(long CommittedBytes, long FragmentedBytes);
