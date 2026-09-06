using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class MeasurementOptionsRangeAnalyzerTests
{
    [Fact]
    public async Task Reports_out_of_range_iterations()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions { Samples = 200000 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_out_of_range_warmup()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions { WarmupSamples = 50000 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_out_of_range_confidence()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions { ConfidenceLevel = 1.5 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_zero_confidence()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions { ConfidenceLevel = 0.0 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_integer_confidence_literal()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions { ConfidenceLevel = 1 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task No_diagnostic_for_valid_values()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions {
                               Samples = 200,
                               WarmupSamples = 25,
                               ConfidenceLevel = 0.95
                           };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_out_of_range_iterations_in_with_expression()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions() with { Samples = 200000 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_out_of_range_warmup_in_with_expression()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions() with { WarmupSamples = 50000 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_out_of_range_confidence_in_with_expression()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions() with { ConfidenceLevel = 1.5 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task No_diagnostic_for_valid_with_expression()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var opts = new MeasurementOptions() with { Samples = 200, ConfidenceLevel = 0.95 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_out_of_range_const_assignment()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           const int samples = 200000;
                           var opts = new MeasurementOptions { Samples = samples };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }

    [Fact]
    public async Task Reports_out_of_range_in_implicit_object_creation()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           MeasurementOptions opts = new() { WarmupSamples = 50000 };
                       }
                   }
                   """;

        await NBAnalyzerVerifier<MeasurementOptionsRangeAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0009");
    }
}
