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
                       private readonly MyDbContext _db;
                       public OrderBenchmarks(MyDbContext db) { _db = db; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                       public System.Threading.Tasks.Task ResetAsync(System.Threading.CancellationToken cancellationToken)
                       {
                           _db.Clear();
                           return System.Threading.Tasks.Task.CompletedTask;
                       }
                   }

                   public class MyDbContext : System.IDisposable
                   {
                       public void Clear() { }
                       public void Dispose() { }
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    /// <summary>
    ///     An empty <c>ResetAsync</c> is the shape that used to buy silence for free - from this
    ///     analyzer and from the engine, both of which could only see that the interface was present.
    ///     The body is the one thing an analyzer can read, so it reads it.
    /// </summary>
    [Fact]
    public async Task Reports_diagnostic_when_IStateReset_implementation_is_empty()
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

        var diagnostics = await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>.GetDiagnosticsAsync(code);
        var message = Assert.Single(diagnostics.Where(d => d.Id == "NB0011")).GetMessage();

        Assert.Contains("ResetAsync body is empty", message, StringComparison.Ordinal);
        Assert.Contains("[SharedState]", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reports_diagnostic_when_IStateReset_implementation_is_an_empty_block()
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
                       public async System.Threading.Tasks.Task ResetAsync(System.Threading.CancellationToken cancellationToken)
                       {
                       }
                   }

                   public class MyDbContext : System.IDisposable
                   {
                       public void Dispose() { }
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0011");
    }

    /// <summary>
    ///     <c>[SharedState]</c> claims nothing a body could contradict, so it silences the rule
    ///     outright.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_when_sharing_is_declared_with_SharedState()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   [SharedState]
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
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    /// <summary>
    ///     The fluent harness default produces exactly the same sharing as the attribute and had no
    ///     compile-time signal at all. It is answered at compilation end, because the call that sets
    ///     it is in a different file from the class it decides for.
    /// </summary>
    [Fact]
    public async Task Reports_diagnostic_when_the_harness_default_is_PerClass()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

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

                   public static class Entry
                   {
                       public static void Main()
                       {
                           new BenchmarkHarness().WithInstanceLifetime(InstanceLifetime.PerClass);
                       }
                   }
                   """;

        var diagnostics = await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>.GetDiagnosticsAsync(code);
        var message = Assert.Single(diagnostics.Where(d => d.Id == "NB0011")).GetMessage();

        Assert.Contains("WithInstanceLifetime(InstanceLifetime.PerClass)", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_diagnostic_when_the_harness_default_is_PerMethod()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

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

                   public static class Entry
                   {
                       public static void Main()
                       {
                           new BenchmarkHarness().WithInstanceLifetime(InstanceLifetime.PerMethod);
                       }
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    /// <summary>
    ///     A class-level <c>[InstanceLifetime(PerMethod)]</c> beats the harness default, so the
    ///     fluent scan must not report it.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_when_the_class_overrides_a_PerClass_harness_default()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerMethod)]
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

                   public static class Entry
                   {
                       public static void Main()
                       {
                           new BenchmarkHarness().WithInstanceLifetime(InstanceLifetime.PerClass);
                       }
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    /// <summary>
    ///     One method cannot observe what it left behind for itself.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_for_PerClass_with_a_single_benchmark_method()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
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

    /// <summary>
    ///     A container resolves an internal constructor perfectly well, so skipping non-public ones
    ///     left the shape most likely to be DI-only as the one shape never checked.
    /// </summary>
    [Fact]
    public async Task Reports_diagnostic_for_a_non_public_constructor()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class OrderBenchmarks
                   {
                       internal OrderBenchmarks(MyDbContext db) { _ = db; }
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

    /// <summary>
    ///     A record's compiler-generated copy constructor takes the record itself as a reference-type
    ///     parameter, so an implicit constructor must not be read as a dependency.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_for_a_record_whose_only_reference_parameter_is_its_copy_constructor()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public record OrderBenchmarks(int Size)
                   {
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
                   }
                   """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyNoDiagnosticAsync(code, "NB0011");
    }

    [Fact]
    public async Task No_diagnostic_for_a_non_generic_ILogger_parameter()
    {
        var code = """
                   using NBenchmark.Attributes;
                   using NBenchmark;

                   [InstanceLifetime(InstanceLifetime.PerClass)]
                   public class OrderBenchmarks
                   {
                       public OrderBenchmarks(Microsoft.Extensions.Logging.ILogger logger) { _ = logger; }
                       [Benchmark] public int A() => 0;
                       [Benchmark] public int B() => 0;
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
