namespace NBenchmark.Engine;

internal readonly record struct DiagnosticDelta(int Gen0, int Gen1, int Gen2, long CpuTimeTicks);
