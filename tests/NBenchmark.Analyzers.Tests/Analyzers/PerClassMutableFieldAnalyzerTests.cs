using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class PerClassMutableFieldAnalyzerTests
{
    [Fact]
    public async Task Reports_diagnostic_when_PerClass_with_mutable_field_accessed_by_two_benchmarks()
    {
        var code = """
                   using NBenchmark;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class CacheBenchmarks
                   {
                       private int _counter;

                       [Benchmark] public int A() => _counter++;
                       [Benchmark] public int B() => _counter++;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassMutableFieldAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0013");
    }

    [Fact]
    public async Task No_diagnostic_for_PerClass_with_readonly_field()
    {
        var code = """
                   using NBenchmark;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class ReadonlyBenchmarks
                   {
                       private readonly int _value = 42;

                       [Benchmark] public int A() => _value;
                       [Benchmark] public int B() => _value;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassMutableFieldAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0013");
    }

    [Fact]
    public async Task No_diagnostic_for_PerClass_with_mutable_field_accessed_by_one_benchmark()
    {
        var code = """
                   using NBenchmark;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class SingleAccessBenchmarks
                   {
                       private int _counter;

                       [Benchmark] public int A() => _counter++;
                       [Benchmark] public int B() => 0;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassMutableFieldAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0013");
    }

    [Fact]
    public async Task No_diagnostic_for_PerMethod_with_mutable_field()
    {
        var code = """
                   using NBenchmark;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerMethod)]
                   public class PerMethodBenchmarks
                   {
                       private int _counter;

                       [Benchmark] public int A() => _counter++;
                       [Benchmark] public int B() => _counter++;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassMutableFieldAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0013");
    }

    [Fact]
    public async Task No_diagnostic_for_PerClass_with_single_benchmark()
    {
        var code = """
                   using NBenchmark;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class SingleBenchmark
                   {
                       private int _counter;

                       [Benchmark] public int A() => _counter++;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassMutableFieldAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0013");
    }
}
