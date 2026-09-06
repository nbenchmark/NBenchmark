using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class BenchmarkCaseConflictAnalyzerTests
{
    [Fact]
    public async Task Reports_when_both_BenchmarkCase_and_BenchmarkCases_are_present()
    {
        var code = """
                   using NBenchmark;
                   using System.Collections.Generic;
                   public class C {
                       [Arguments(10)]
                       [ArgumentsSource(nameof(Cases))]
                       [Benchmark]
                       public void M(int a) { }
                       public static IEnumerable<(int x)> Cases() { yield return (1,); }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseConflictAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0012");
    }

    [Fact]
    public async Task No_diagnostic_when_only_BenchmarkCase()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       [Arguments(42)]
                       [Benchmark]
                       public void M(int x) { }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseConflictAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0012");
    }

    [Fact]
    public async Task No_diagnostic_when_only_BenchmarkCases()
    {
        var code = """
                   using NBenchmark;
                   using System.Collections.Generic;
                   public class C {
                       [ArgumentsSource(nameof(Cases))]
                       [Benchmark]
                       public void M(int x) { }
                       public static IEnumerable<(int a)> Cases() { yield return (1,); }
                   }
                   """;

        await NBAnalyzerVerifier<BenchmarkCaseConflictAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0012");
    }
}
