using System.Collections.Generic;
using NBenchmark.Attributes;

namespace NBenchmark.Tests.ErrorFixtures;

public class ParamSourceCasesBenchmarks
{
    [BenchmarkCases(nameof(BadSource))]
    [Benchmark]
    public int Square(int a) => a * a;

    public static IEnumerable<ValueTuple<int>> BadSource(int x)
    {
        yield return ValueTuple.Create(x);
    }
}
