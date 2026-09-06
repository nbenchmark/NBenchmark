; Unshipped analyzer release ; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

 Rule ID | Category                 | Severity | Notes
---------|--------------------------|----------|--------------------------------------------------------------------
 NB0001  | NBenchmark.Usage         | Warning  | Benchmark class must have a public parameterless constructor.
 NB0002  | NBenchmark.Usage         | Error    | [Benchmark] method must not be static.
 NB0003  | NBenchmark.Usage         | Error    | [BenchmarkCase] / [BenchmarkCases] must match method parameters.
 NB0004  | NBenchmark.Performance   | Error    | [Benchmark] body has no observable side effects.
 NB0005  | NBenchmark.Performance   | Error    | [Benchmark] body does no observable work.
 NB0006  | NBenchmark.Configuration | Error    | Only one [Benchmark(Baseline = true)] allowed per class.
 NB0007  | NBenchmark.Usage         | Error    | Duplicate lifecycle method in benchmark class.
 NB0008  | NBenchmark.Configuration | Error    | [Benchmark] property value out of range.
 NB0009  | NBenchmark.Configuration | Error    | MeasurementOptions property value out of range.
 NB0010  | NBenchmark.Performance   | Warning  | Benchmark body appears to be throwaway.
 NB0011  | NBenchmark.Usage         | Warning  | PerClass instance lifetime with a scoped service.
 NB0012  | NBenchmark.Usage         | Error    | [BenchmarkCases] cannot be combined with [BenchmarkCase].
 NB0013  | NBenchmark.Usage         | Warning  | PerClass instance lifetime with a mutable instance field.
 NB0014  | NBenchmark.Performance   | Info     | Benchmark body captures state, which may prevent isolation.
