using NBenchmark;

namespace NBenchmark.Tests.ErrorFixtures;

public class ArityMismatchCasesBenchmarks
{
    [ArgumentsSource(nameof(MismatchCases))]
    [Benchmark]
    public int Sum(int a, int b) => a + b;

    public static IEnumerable<ValueTuple<int>> MismatchCases()
    {
        yield return ValueTuple.Create(1);
    }
}
