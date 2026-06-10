using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class ThrowawayBodyAnalyzerTests
{
    [Fact]
    public async Task Reports_empty_lambda()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.Run(() => { });
                       }
                   }
                   """;

        await NBAnalyzerVerifier<ThrowawayBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0010");
    }

    [Fact]
    public async Task Reports_noop_lambda()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.Run(() => { var x = 42; });
                       }
                   }
                   """;

        await NBAnalyzerVerifier<ThrowawayBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0010");
    }

    [Fact]
    public async Task No_diagnostic_when_lambda_assigns_field()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       private int _x;
                       public void M() {
                           Benchmark.Run(() => { _x = 42; });
                       }
                   }
                   """;

        await NBAnalyzerVerifier<ThrowawayBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0010");
    }

    [Fact]
    public async Task No_diagnostic_when_lambda_has_side_effect()
    {
        var code = """
                   using System;
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.Run(() => Console.WriteLine("hi"));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<ThrowawayBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0010");
    }

    [Fact]
    public async Task Reports_throwaway_lambda_for_runraw_action_overload()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.RunRaw(() => { var x = 42; });
                       }
                   }
                   """;

        await NBAnalyzerVerifier<ThrowawayBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0010");
    }

    [Fact]
    public async Task No_diagnostic_for_value_returning_run_overload()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           _ = Benchmark.Run(() => 42);
                       }
                   }
                   """;

        await NBAnalyzerVerifier<ThrowawayBodyAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0010");
    }
}
