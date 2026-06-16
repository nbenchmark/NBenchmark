using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

public sealed class PerClassWithScopedServiceAnalyzerTests
{
    [Fact]
    public async Task Reports_diagnostic_when_PerClass_with_DbContext_ctor_parameter()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class OrderBenchmarks
                   {
                       public OrderBenchmarks(MyDbContext db) { _ = db; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                   }

                   public class MyDbContext : System.IDisposable
                   {
                       public void Dispose() { }
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0011");
    }

    [Fact]
    public async Task No_diagnostic_for_PerMethod_with_DbContext()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerMethod)]
                   public class OrderBenchmarks
                   {
                       public OrderBenchmarks(MyDbContext db) { _ = db; }
                       [Benchmark] public int A() => 0;
                   }

                   public class MyDbContext : System.IDisposable
                   {
                       public void Dispose() { }
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    [Fact]
    public async Task No_diagnostic_for_PerClass_with_primitive_ctor_parameter()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class StringBenchmarks
                   {
                       public StringBenchmarks(int seed) { _ = seed; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    [Fact]
    public async Task No_diagnostic_for_disposable_parameter_that_doesnt_match_heuristic()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class OrderBenchmarks
                   {
                       public OrderBenchmarks(IWorker worker) { _ = worker; }
                       [Benchmark] public int A() => 0;
                   }

                   public interface IWorker { int Do(); }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    [Fact]
    public async Task No_diagnostic_for_PerClass_with_string_context_parameter()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class StringContextBenchmarks
                   {
                       public StringContextBenchmarks(StringContext ctx) { _ = ctx; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                   }

                   public sealed class StringContext
                   {
                       public string Value { get; set; } = "";
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    [Fact]
    public async Task No_diagnostic_for_PerClass_with_HttpContext_parameter()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class HttpContextBenchmarks
                   {
                       public HttpContextBenchmarks(Microsoft.AspNetCore.Http.HttpContext ctx) { _ = ctx; }
                       [Benchmark] public int A() => 0;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    [Fact]
    public async Task No_diagnostic_for_underspecified_DbContext_on_single_method_permethod()
    {
        // The PerMethod variant should never trigger NB0011 regardless of other
        // characteristics; this is the canonical "DB context with one method
        // and PerMethod lifetime" case the analyzer should leave alone.
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerMethod)]
                   public class OrderBenchmarks
                   {
                       public OrderBenchmarks(MyDbContext db) { _ = db; }
                       [Benchmark] public int A() => 0;
                   }

                   public class MyDbContext : System.IDisposable { public void Dispose() { } }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }
}
