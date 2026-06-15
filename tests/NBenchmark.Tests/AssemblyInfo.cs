using Xunit;

// Benchmarks measure wall-clock time, so running tests in parallel would let
// unrelated suites contend for CPU and skew timings. Serial execution also keeps
// the process-global suite invocation-ordinal sequence (used by isolated replay)
// deterministic for the isolation tests, which reset and assert exact ordinals.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
