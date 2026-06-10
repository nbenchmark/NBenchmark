using NBenchmark.Analyzers.Analyzers;
using NBenchmark.CodeFixes.CodeFixes;

namespace NBenchmark.Analyzers.Tests.CodeFixes;

public sealed class RemoveStaticKeywordCodeFixTests
{
    [Fact]
    public async Task Analyzer_detects_static_method()
    {
        var source = """
                     using NBenchmark.Attributes;
                     public class C {
                         [Benchmark]
                         public static void M() { }
                     }
                     """;

        await NBAnalyzerVerifier<StaticBenchmarkMethodAnalyzer>
            .VerifyAnalyzerAsync(source, "NB0002");
    }

    [Fact]
    public async Task No_diagnostic_for_instance_method()
    {
        var source = """
                     using NBenchmark.Attributes;
                     public class C {
                         [Benchmark]
                         public void M() { }
                     }
                     """;

        await NBAnalyzerVerifier<StaticBenchmarkMethodAnalyzer>
            .VerifyNoDiagnosticAsync(source, "NB0002");
    }

    [Fact]
    public async Task CodeFix_removes_static_modifier()
    {
        var source = """
                     using NBenchmark.Attributes;
                     public class C {
                         [Benchmark]
                         public static void M() { }
                     }
                     """;

        var fixedSource = """
                          using NBenchmark.Attributes;
                          public class C {
                              [Benchmark]
                              public void M() { }
                          }
                          """;

        await NBAnalyzerVerifier<StaticBenchmarkMethodAnalyzer>
            .VerifyCodeFixAsync<RemoveStaticKeywordCodeFixProvider>(source, fixedSource, "NB0002");
    }
}
