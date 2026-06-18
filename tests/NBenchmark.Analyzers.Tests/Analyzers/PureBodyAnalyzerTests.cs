using Microsoft.CodeAnalysis;
using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class PureBodyAnalyzerTests
{
    [Fact]
    public async Task Reports_empty_body()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0005", DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Reports_body_with_only_local_var()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() { var x = 42; }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0004", DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Reports_body_with_empty_for_loop()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() { for (var i = 0; i < 1000; i++) { } }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0004", DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Reports_diagnostic_when_incrementing_local()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() { int x = 0; x++; }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0004", DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task No_diagnostic_when_calling_method()
    {
        var code = """
                   using System;
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() { Console.WriteLine("hi"); }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }

    [Fact]
    public async Task No_diagnostic_when_assigning_field()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       private int _x;
                       [Benchmark]
                       public void M() { _x = 42; }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }

    [Fact]
    public async Task No_diagnostic_for_returning_method()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public int M() => 42;
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }

    [Fact]
    public async Task No_diagnostic_for_new_object()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() { new object(); }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }

    [Fact]
    public async Task No_diagnostic_for_expression_body_with_method_call()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark]
                       public void M() => System.Math.Sqrt(2);
                   }
                   """;

        // The analyzer flags ANY invocation at the syntax level (it cannot
        // distinguish pure functions from side-effecting ones). This test documents
        // the current behavior: Math.Sqrt(2) is treated as a potential side effect.
        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }

    [Fact]
    public async Task No_diagnostic_when_incrementing_field()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       private int _x;
                       [Benchmark]
                       public void M() { _x++; }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }

    [Fact]
    public async Task No_diagnostic_when_decrementing_field()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       private int _x;
                       [Benchmark]
                       public void M() { --_x; }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }

    [Fact]
    public async Task No_diagnostic_when_incrementing_property()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       public int Counter { get; set; }
                       [Benchmark]
                       public void M() { Counter++; }
                   }
                   """;

        await NBAnalyzerVerifier<PureBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0004");
    }
}
