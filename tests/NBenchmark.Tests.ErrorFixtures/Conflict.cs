using NBenchmark.Attributes;

namespace NBenchmark.Tests.ErrorFixtures;

public class ConflictCasesBenchmarks
{
    [BenchmarkCase(10)]
    [BenchmarkCases(nameof(Cases))]
    [Benchmark]
    public int Both(int a) => a;

    public static IEnumerable<ValueTuple<int>> Cases()
    {
        yield return ValueTuple.Create(1);
    }
}
