using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class MultipleBaselinesAnalyzerTests
{
    [Fact]
    public async Task Reports_second_baseline()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [Benchmark(Baseline = true)] public void A() { }
                [Benchmark(Baseline = true)] public void B() { }
            }
            """;

        await NBAnalyzerVerifier<MultipleBaselinesAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0006");
    }

    [Fact]
    public async Task No_diagnostic_for_single_baseline()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [Benchmark(Baseline = true)] public void A() { }
                [Benchmark] public void B() { }
            }
            """;

        await NBAnalyzerVerifier<MultipleBaselinesAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0006");
    }

    [Fact]
    public async Task No_diagnostic_when_no_baseline()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [Benchmark] public void A() { }
                [Benchmark] public void B() { }
            }
            """;

        await NBAnalyzerVerifier<MultipleBaselinesAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0006");
    }

    [Fact]
    public async Task No_diagnostic_for_single_baseline_true_and_false()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [Benchmark(Baseline = true)] public void A() { }
                [Benchmark(Baseline = false)] public void B() { }
            }
            """;

        await NBAnalyzerVerifier<MultipleBaselinesAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0006");
    }
}