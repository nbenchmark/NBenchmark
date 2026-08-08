using NBenchmark.Attributes;

namespace NBenchmark.Tests.ErrorFixtures;

/// <summary>
///     A class whose instances could only come from a container, with an <b>instance</b>
///     <c>[BenchmarkCases]</c> source.
/// </summary>
/// <remarks>
///     Case values decide how many benchmarks there are, so discovery needs them before any instance
///     exists - and it has only the type's own constructor to work with, which this type does not have.
///     The combination used to fault the group with a bare "No parameterless constructor defined",
///     which sends the reader looking for a constructor they deliberately did not write.
///     <para>
///         Lives here rather than beside its test because it throws during discovery, and a
///         whole-assembly pass over the test project would take every other discovery test with it.
///     </para>
/// </remarks>
public class InjectedCaseSourceBenchmarks(int scale)
{
    [BenchmarkCases(nameof(Cases))]
    [Benchmark]
    public int Compute(int n) => n * scale;

    public IEnumerable<ValueTuple<int>> Cases()
    {
        yield return new ValueTuple<int>(scale);
    }
}
