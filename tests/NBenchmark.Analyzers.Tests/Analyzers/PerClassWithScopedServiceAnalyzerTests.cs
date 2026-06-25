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
    public async Task No_diagnostic_when_PerClass_with_scoped_service_implements_IStateReset()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;
                   using NBenchmark.Lifecycle;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class OrderBenchmarks : IStateReset
                   {
                       public OrderBenchmarks(MyDbContext db) { _ = db; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                       public System.Threading.Tasks.Task ResetAsync(System.Threading.CancellationToken cancellationToken)
                           => System.Threading.Tasks.Task.CompletedTask;
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
    public async Task No_diagnostic_for_PerClass_with_string_ctor_parameter()
    {
        // string is a reference type but immutable; sharing it cannot contaminate state.
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class ParserBenchmarks
                   {
                       public ParserBenchmarks(string input) { _ = input; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    [Fact]
    public async Task Reports_diagnostic_for_PerClass_with_non_disposable_reference_type()
    {
        // The broadened heuristic now flags any non-primitive, non-ambient reference type.
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class CacheBenchmarks
                   {
                       public CacheBenchmarks(IMemoryCache cache) { _ = cache; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                   }

                   public interface IMemoryCache { int Get(); }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0011");
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
