using NBenchmark;
using NBenchmark.Reporters;
using NBenchmark.Reporters.Console;

// Run 1: Simple - one table, counts footer. The default. Targeted at the
// average developer who just wants "is A faster than B?"
await new BenchmarkSuite("sorting-simple")
    .Add("bubble", () => { var a = Enumerable.Range(0, 100).Reverse().ToArray(); Array.Sort(a); })
    .Add("linq",   () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmup(3).WithIterations(50)
    .WithDetail(ReportDetail.Simple)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Run 2: Standard - full comparison table + precision/tail latency + auto-tune
// + interpretation. For practitioners who want to understand variability.
await new BenchmarkSuite("sorting-standard")
    .Add("bubble", () => { var a = Enumerable.Range(0, 100).Reverse().ToArray(); Array.Sort(a); })
    .Add("linq",   () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmup(3).WithIterations(50)
    .WithDetail(ReportDetail.Standard)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Run 3: Advanced - everything in Standard plus a per-benchmark distribution
// details block (quartiles, fences, skew, kurt, MAD, Cliff's delta, allocation
// breakdown). For deep statistical analysis.
await new BenchmarkSuite("sorting-advanced")
    .Add("bubble", () => { var a = Enumerable.Range(0, 100).Reverse().ToArray(); Array.Sort(a); })
    .Add("linq",   () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmup(3).WithIterations(50)
    .WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
