using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class DuplicateLifecycleMethodAnalyzerTests
{
    [Fact]
    public async Task Reports_second_setup()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       [GlobalSetup] public void Setup1() { }
                       [GlobalSetup] public void Setup2() { }
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
                   using NBenchmark;
                   public class C {
                       [GlobalTeardown] public void Tear1() { }
                       [GlobalTeardown] public void Tear2() { }
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
                   using NBenchmark;
                   public class C {
                       [SampleSetup] public void Iter1() { }
                       [SampleSetup] public void Iter2() { }
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
                   using NBenchmark;
                   public class C {
                       [GlobalSetup] public void Setup() { }
                       [GlobalTeardown] public void Tear() { }
                       [SampleSetup] public void IterSetup() { }
                       [SampleTeardown] public void IterTear() { }
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
                   using NBenchmark;
                   public class C {
                       [GlobalSetup] public void Setup() { }
                       [GlobalSetup] public void Setup2() { }
                   }
                   """;

        await NBAnalyzerVerifier<DuplicateLifecycleMethodAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0007");
    }
}
