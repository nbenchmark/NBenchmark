using NBenchmark.Attributes;

namespace NBenchmark.Tests.ErrorFixtures;

/// <summary>
///     A class asking for both the host process and a dedicated worker.
/// </summary>
/// <remarks>
///     The two attributes cannot both be honoured, and resolving the conflict silently - which is what
///     discovery used to do, in favour of <c>[InProcess]</c> - discards a request about where the
///     measurement runs. Analyzer NB0015 catches it in source; discovery refuses it for assemblies no
///     analyzer ever saw.
///     <para>
///         Lives here rather than beside its test for the same reason
///         <see cref="InjectedCaseSourceBenchmarks" /> does: it throws during discovery, so a
///         whole-assembly pass over the test project would take every other discovery test with it.
///     </para>
/// </remarks>
[InProcess]
[IsolatedProcess]
public class ConflictingIsolationBenchmarks
{
    [Benchmark]
    public int Body() => 1;
}
