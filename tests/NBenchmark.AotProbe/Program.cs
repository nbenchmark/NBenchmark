// The Native AOT probe. NBenchmark cannot be AOT-clean as a whole - discovery, argument binding and
// the worker protocol are reflective by design - so this asks the narrower and more useful question:
// what actually happens when a consumer publishes the simplest possible use of it, a single-mode
// Benchmark.Run over a non-capturing lambda, with PublishAot=true?
//
// The answer today is checked in below, and it is the two-part answer nothing else records:
//
//   * The publish succeeds. Every reflective member NBenchmark reaches is declared, so the compiler
//     reports the three diagnostics the csproj accepts and nothing else. A new, undeclared hazard
//     fails the publish before this program ever runs.
//   * The run refuses. Measuring dispatches to the engine's typed entry point through
//     MethodInfo.MakeGenericMethod - the delegate arrives as a Delegate and its T is recovered at
//     run time - and AOT has no native code for that instantiation. So single mode is *not* yet
//     AOT-viable, and the failure is a clean NotSupportedException rather than a wrong number.
//
// The exit code encodes that expectation, which makes this a gate in both directions: an
// undeclared trim/AOT hazard breaks the publish, and making single mode genuinely AOT-viable breaks
// this program - at which point the fact recorded here is out of date and wants updating, along
// with docs/reference/aot.md.

using NBenchmark;

var options = new MeasurementOptions
{
    // No worker: an AOT-published app locates nbworker through Assembly.Location, which is empty in
    // a single-file image. Asked for explicitly so the probe measures the in-process path on
    // purpose rather than arriving there through the fallback.
    Isolation = Isolation.Off,
    Samples = 16,
    WarmupSamples = 4,
    OpsPerSample = 512,
};

try
{
    var result = Benchmark.Run(() => Fibonacci(15), options, "aot-probe");

    Console.WriteLine($"median={result.MedianNs:F2}ns isolation={result.IsolationStatus}");
    Console.Error.WriteLine(
        "A single-mode run completed under Native AOT. That is an improvement, not a failure: "
        + "update this probe to assert success, and update docs/reference/aot.md, which still says "
        + "single mode refuses.");

    return 1;
}
catch (NotSupportedException ex) when (ex.Message.Contains("MakeGenericMethod", StringComparison.Ordinal))
{
    // The documented refusal. Printed rather than swallowed so a CI log shows which member refused.
    Console.WriteLine($"expected refusal under Native AOT: {ex.Message.Split('\n')[0]}");
    return 0;
}

static int Fibonacci(int n)
{
    var (a, b) = (0, 1);

    for (var i = 0; i < n; i++)
        (a, b) = (b, a + b);

    return a;
}
