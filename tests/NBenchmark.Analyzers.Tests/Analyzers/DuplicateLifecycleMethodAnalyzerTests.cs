using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class DuplicateLifecycleMethodAnalyzerTests
{
    [Fact]
    public async Task Reports_second_setup()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [BenchmarkSetup] public void Setup1() { }
                [BenchmarkSetup] public void Setup2() { }
                [Benchmark] public void M() { }
            }
            """;

        await NBAnalyzerVerifier<DuplicateLifecycleMethodAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0007");
    }

    [Fact]
    public async Task Reports_second_teardown()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [BenchmarkTeardown] public void Tear1() { }
                [BenchmarkTeardown] public void Tear2() { }
                [Benchmark] public void M() { }
            }
            """;

        await NBAnalyzerVerifier<DuplicateLifecycleMethodAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0007");
    }

    [Fact]
    public async Task Reports_second_iteration_setup()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [BenchmarkIterationSetup] public void Iter1() { }
                [BenchmarkIterationSetup] public void Iter2() { }
                [Benchmark] public void M() { }
            }
            """;

        await NBAnalyzerVerifier<DuplicateLifecycleMethodAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0007");
    }

    [Fact]
    public async Task No_diagnostic_when_single_each()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [BenchmarkSetup] public void Setup() { }
                [BenchmarkTeardown] public void Tear() { }
                [BenchmarkIterationSetup] public void IterSetup() { }
                [BenchmarkIterationTeardown] public void IterTear() { }
                [Benchmark] public void M() { }
            }
            """;

        await NBAnalyzerVerifier<DuplicateLifecycleMethodAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0007");
    }

    [Fact]
    public async Task No_diagnostic_for_class_without_benchmarks()
    {
        var code = """
            using NBenchmark.Attributes;
            public class C {
                [BenchmarkSetup] public void Setup() { }
                [BenchmarkSetup] public void Setup2() { }
            }
            """;

        await NBAnalyzerVerifier<DuplicateLifecycleMethodAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0007");
    }
}