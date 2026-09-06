using NBenchmark.Attributes;
using NBenchmark.Engine;
using Xunit;

namespace NBenchmark.Tests
{
    public class W40FullNameDisambiguationTests
    {
        [Fact]
        public async Task Harness_PerClassSignificance_DoesNotCollide_WhenSimpleClassNamesMatchAcrossNamespaces()
        {
            var harness = (BenchmarkHarness)Activator.CreateInstance(typeof(BenchmarkHarness), true)!;

            harness
                .AddFromAssembly(typeof(W40FullNameDisambiguationTests).Assembly)
                .WithCategoryFilter(["w40-fullname"])
                .WithOptions(new MeasurementOptions
                {
                    Iterations = 8,
                    WarmupIterations = 0,
                    OutlierMode = OutlierMode.None,
                })
                .WithIsolation(Isolation.Off);

            var results = await harness.RunAsync();

            Assert.Equal(4, results.Count);
            Assert.Equal(4, results.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(2, results.Select(r => r.ClassName).Distinct(StringComparer.Ordinal).Count());

            Assert.Contains(typeof(NBenchmark.Tests.W40.Left.Bench).FullName, results.Select(r => r.ClassName));
            Assert.Contains(typeof(NBenchmark.Tests.W40.Right.Bench).FullName, results.Select(r => r.ClassName));

            Assert.Equal(2, results.Count(r => r.IsBaseline));
            Assert.All(results, r => Assert.False(r.Errored, r.ErrorMessage));
        }
    }
}

namespace NBenchmark.Tests.W40.Left
{
    [BenchmarkCategory("w40-fullname")]
    public class Bench
    {
        [Benchmark(Baseline = true)]
        public void Fast() => Thread.SpinWait(32);

        [Benchmark]
        public void Slow() => Thread.SpinWait(128);
    }
}

namespace NBenchmark.Tests.W40.Right
{
    [BenchmarkCategory("w40-fullname")]
    public class Bench
    {
        [Benchmark(Baseline = true)]
        public void Fast() => Thread.SpinWait(48);

        [Benchmark]
        public void Slow() => Thread.SpinWait(192);
    }
}
