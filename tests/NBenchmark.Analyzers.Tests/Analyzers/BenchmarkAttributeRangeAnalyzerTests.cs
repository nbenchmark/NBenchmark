using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class BenchmarkAttributeRangeAnalyzerTests
{
    [Fact]
    public async Task Reports_negative_iterations()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark(Iterations = -5)] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkAttributeRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0008");
    }

    [Fact]
    public async Task Reports_excessive_iterations()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark(Iterations = 200000)] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkAttributeRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0008");
    }

    [Fact]
    public async Task Reports_excessive_warmup()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark(WarmupIterations = 50000)] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkAttributeRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0008");
    }

    [Fact]
    public async Task No_diagnostic_for_default_minus_one_sentinel()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark(Iterations = -1)] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkAttributeRangeAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0008");
    }
}
