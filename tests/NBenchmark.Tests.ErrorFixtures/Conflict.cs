using NBenchmark;

namespace NBenchmark.Tests.ErrorFixtures;

public class ConflictCasesBenchmarks
{
    [Arguments(10)]
    [ArgumentsSource(nameof(Cases))]
    [Benchmark]
    public int Both(int a) => a;

    public static IEnumerable<ValueTuple<int>> Cases()
    {
        yield return ValueTuple.Create(1);
    }
}
