using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class StaticBenchmarkMethodAnalyzerTests
{
    [Fact]
    public async Task Reports_static_method()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [Benchmark]
                public static void M() { }
            }
            """;

        await NBAnalyzerVerifier<StaticBenchmarkMethodAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0002");
    }

    [Fact]
    public async Task No_diagnostic_for_instance_method()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [Benchmark]
                public void M() { }
            }
            """;

        await NBAnalyzerVerifier<StaticBenchmarkMethodAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0002");
    }

    [Fact]
    public async Task No_diagnostic_for_static_non_benchmark()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                public static void M() { }
                [Benchmark]
                public void N() { }
            }
            """;

        await NBAnalyzerVerifier<StaticBenchmarkMethodAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0002");
    }
}