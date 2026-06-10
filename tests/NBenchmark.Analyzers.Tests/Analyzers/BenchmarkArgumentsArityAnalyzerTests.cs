using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class BenchmarkArgumentsArityAnalyzerTests
{
    [Fact]
    public async Task Reports_method_with_parameters_but_no_arguments_attr()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M(int x) { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkArgumentsArityAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0003");
    }

    [Fact]
    public async Task Reports_method_with_arguments_but_no_parameters()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [BenchmarkArguments(42)]
                       [Benchmark]
                       public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkArgumentsArityAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0003");
    }

    [Fact]
    public async Task No_diagnostic_when_arity_matches()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [BenchmarkArguments(42)]
                       [Benchmark]
                       public void M(int x) { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkArgumentsArityAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0003");
    }

    [Fact]
    public async Task No_diagnostic_for_parameterless_method()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkArgumentsArityAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0003");
    }
}
