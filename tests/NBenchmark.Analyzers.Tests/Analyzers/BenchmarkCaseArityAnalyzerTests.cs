using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class BenchmarkCaseArityAnalyzerTests
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

        await NBAnalyzerVerifier<BenchmarkCaseArityAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0003");
    }

    [Fact]
    public async Task Reports_method_with_case_but_no_parameters()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [BenchmarkCase(42)]
                       [Benchmark]
                       public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseArityAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0003");
    }

    [Fact]
    public async Task Reports_method_with_cases_but_no_parameters()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using System.Collections.Generic;
                   public class C {
                       [BenchmarkCases(nameof(Cases))]
                       [Benchmark]
                       public void M() { }
                       public static IEnumerable<(int x)> Cases() { yield return (1,); }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseArityAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0003");
    }

    [Fact]
    public async Task No_diagnostic_when_case_arity_matches()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [BenchmarkCase(42)]
                       [Benchmark]
                       public void M(int x) { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseArityAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0003");
    }

    [Fact]
    public async Task No_diagnostic_when_cases_arity_matches()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using System.Collections.Generic;
                   public class C {
                       [BenchmarkCases(nameof(Cases))]
                       [Benchmark]
                       public void M(int x, string y) { }
                       public static IEnumerable<System.ValueTuple<int, string>> Cases() { yield return System.ValueTuple.Create(1, "x"); }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseArityAnalyzer>
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

        await NBAnalyzerVerifier<BenchmarkCaseArityAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0003");
    }

    [Fact]
    public async Task Reports_cases_member_not_found()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using System.Collections.Generic;
                   public class C {
                       [BenchmarkCases("NonExistent")]
                       [Benchmark]
                       public void M(int x) { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseArityAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0003");
    }
}
