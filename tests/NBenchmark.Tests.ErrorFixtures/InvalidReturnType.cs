using NBenchmark;

namespace NBenchmark.Tests.ErrorFixtures;

public class InvalidReturnTypeCasesBenchmarks
{
    [ArgumentsSource(nameof(BadCases))]
    [Benchmark]
    public int Sum(int a) => a;

    public static IEnumerable<int> BadCases()
    {
        yield return 1;
    }
}
