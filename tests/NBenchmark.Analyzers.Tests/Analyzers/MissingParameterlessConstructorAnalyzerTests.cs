using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class MissingParameterlessConstructorAnalyzerTests
{
    [Fact]
    public async Task Reports_class_without_parameterless_ctor()
    {
        var code = """
                   using NBenchmark;
                   public class Bench
                   {
                       private readonly int _x;
                       public Bench(int x) { _x = x; }
                       [Benchmark] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0001");
    }

    [Fact]
    public async Task No_diagnostic_for_class_with_implicit_ctor()
    {
        var code = """
                   using NBenchmark;
                   public class Bench
                   {
                       [Benchmark] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0001");
    }

    [Fact]
    public async Task No_diagnostic_for_class_with_explicit_parameterless_ctor()
    {
        var code = """
                   using NBenchmark;
                   public class Bench
                   {
                       public Bench() { }
                       [Benchmark] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0001");
    }

    [Fact]
    public async Task No_diagnostic_for_class_without_benchmarks()
    {
        var code = """
                   public class NotABench
                   {
                       public NotABench(int x) { }
                       public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0001");
    }

    [Fact]
    public async Task No_diagnostic_for_abstract_class()
    {
        var code = """
                   using NBenchmark;
                   public abstract class Bench
                   {
                       protected Bench(int x) { }
                       [Benchmark] public abstract void M();
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0001");
    }

    [Fact]
    public async Task Reports_record_without_parameterless_ctor()
    {
        var code = """
                   using NBenchmark;
                   public record Bench(int X)
                   {
                       [Benchmark] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0001");
    }

    [Fact]
    public async Task No_diagnostic_for_record_without_benchmarks()
    {
        var code = """
                   public record Bench(int X)
                   {
                       public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0001");
    }

    [Fact]
    public async Task No_diagnostic_for_derived_class_with_no_own_benchmarks()
    {
        var code = """
                   using NBenchmark;
                   public abstract class Base
                   {
                       [Benchmark] public void M() { }
                   }
                   public class Derived : Base
                   {
                       public Derived(int x) { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0001");
    }

    [Fact]
    public async Task No_diagnostic_for_record_struct_with_primary_ctor()
    {
        var code = """
                   using NBenchmark;
                   public record struct Bench(int X)
                   {
                       [Benchmark] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<MissingParameterlessConstructorAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0001");
    }
}
