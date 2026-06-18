using System.Collections.Generic;
using NBenchmark.Attributes;

namespace NBenchmark.Tests.ErrorFixtures;

public class InvalidReturnTypeCasesBenchmarks
{
    [BenchmarkCases(nameof(BadCases))]
    [Benchmark]
    public int Sum(int a) => a;

    public static IEnumerable<int> BadCases()
    {
        yield return 1;
    }
}
