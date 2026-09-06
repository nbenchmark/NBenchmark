using NBenchmark;
using NBenchmark.Reporters;
using NBenchmark.Reporters.Console;

// Run 1: Simple - one table, counts footer. The default. Targeted at the
// average developer who just wants "is A faster than B?"
//
// Measurement is pinned (OpsPerSample=1 disables ops-per-sample auto-calibration,
// so each run times the same K body invocations per sample; warmup 25 / samples
// 500 give the JIT time to settle and tighten the confidence interval). Only the
// reporter rendering differs across the three runs.
await new BenchmarkSuite("sorting-simple")
    .Add("bubble", () =>
    {
        var a = Enumerable.Range(0, 100).Reverse().ToArray();
        Array.Sort(a);
    })
    .Add("linq", () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmupSamples(25).WithSamples(500).WithOpsPerSample(1)
    .WithDetail(ReportDetail.Simple)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Run 2: Standard - full comparison table + precision/tail latency
// + interpretation. For practitioners who want to understand variability.
//
// Same pinned measurement as Run 1. Standard/Advanced *display* the auto-tune
// diagnostic block; they do not switch measurement modes. Ops-per-sample
// auto-calibration is a measurement concern governed by OpsPerSample (null =
// auto, a pinned value = fixed), not by ReportDetail. All three runs here pin
// OpsPerSample=1 so calibration does not vary run to run.
await new BenchmarkSuite("sorting-standard")
    .Add("bubble", () =>
    {
        var a = Enumerable.Range(0, 100).Reverse().ToArray();
        Array.Sort(a);
    })
    .Add("linq", () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmupSamples(25).WithSamples(500).WithOpsPerSample(1)
    .WithDetail(ReportDetail.Standard)
    .WithReporter(new ConsoleReporter())
    .RunAsync();

// Run 3: Advanced - everything in Standard plus a per-benchmark distribution
// details block (quartiles, fences, skew, kurt, MAD, Cliff's delta, allocation
// breakdown). For deep statistical analysis.
//
// Same pinned measurement as Runs 1 and 2.
await new BenchmarkSuite("sorting-advanced")
    .Add("bubble", () =>
    {
        var a = Enumerable.Range(0, 100).Reverse().ToArray();
        Array.Sort(a);
    })
    .Add("linq", () => { _ = Enumerable.Range(0, 100).Reverse().OrderBy(x => x).ToArray(); })
    .WithBaseline("bubble")
    .WithWarmupSamples(25).WithSamples(500).WithOpsPerSample(1)
    .WithDetail(ReportDetail.Advanced)
    .WithReporter(new ConsoleReporter())
    .RunAsync();
