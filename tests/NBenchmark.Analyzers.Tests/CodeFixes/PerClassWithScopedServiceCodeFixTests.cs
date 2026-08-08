using NBenchmark.Analyzers.Analyzers;
using NBenchmark.CodeFixes.CodeFixes;

namespace NBenchmark.Analyzers.Tests.CodeFixes;

public sealed class PerClassWithScopedServiceCodeFixTests
{
    [Fact]
    public async Task CodeFix_Changes_PerClass_To_PerMethod()
    {
        var source = """
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

        var fixedSource = """
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
                          """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyCodeFixAsync<PerClassWithScopedServiceCodeFixProvider>(source, fixedSource, "NB0011");
    }

    [Fact]
    public async Task CodeFix_Changes_PerClass_To_PerMethod_With_NonDisposable_Reference_Type()
    {
        // Confirms the fix works for the broadened heuristic (any reference-type ctor param).
        var source = """
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

        var fixedSource = """
                          using NBenchmark.Attributes;
                          using NBenchmark;

                          [InstanceLifetime(InstanceLifetime.PerMethod)]
                          public class CacheBenchmarks
                          {
                              public CacheBenchmarks(IMemoryCache cache) { _ = cache; }
                              [Benchmark] public int A() => 0;
                              [Benchmark] public int B() => 0;
                          }

                          public interface IMemoryCache { int Get(); }
                          """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyCodeFixAsync<PerClassWithScopedServiceCodeFixProvider>(source, fixedSource, "NB0011");
    }

    /// <summary>
    ///     The generated <c>ResetAsync</c> must not compile away quietly.
    /// </summary>
    /// <remarks>
    ///     It used to emit <c>return Task.CompletedTask;</c>, which meant the shipped one-click fix
    ///     for this diagnostic silenced the diagnostic <i>and</i> the engine's PerClass safeguard -
    ///     both of which key on the interface being present, not on the body doing anything - while
    ///     resetting nothing. Accepting the fix and moving on was the fastest route to the exact
    ///     contamination the diagnostic reports.
    /// </remarks>
    [Fact]
    public async Task CodeFix_Implements_IStateReset_When_Selected()
    {
        var source = """
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

        var fixedSource = """
                          using NBenchmark.Attributes;
                          using NBenchmark;
                          using NBenchmark.Lifecycle;

                          [InstanceLifetime(InstanceLifetime.PerClass)]
                          public class OrderBenchmarks : global::NBenchmark.Lifecycle.IStateReset
                          {
                              public OrderBenchmarks(MyDbContext db) { _ = db; }
                              [Benchmark] public int A() => 0;
                              [Benchmark] public int B() => 0;

                              public System.Threading.Tasks.Task ResetAsync(System.Threading.CancellationToken cancellationToken)
                              {
                                  // TODO: reset the state shared between [Benchmark] methods.
                                  throw new System.NotImplementedException("Reset the state this class shares between [Benchmark] methods, or replace IStateReset with [SharedState] if the carry-over is deliberate.");
                              }
                          }

                          public class MyDbContext : System.IDisposable
                          {
                              public void Dispose() { }
                          }
                          """;

        await NBAnalyzerVerifier<PerClassWithScopedServiceAnalyzer>
            .VerifyCodeFixAsync<PerClassWithScopedServiceCodeFixProvider>(
                source,
                fixedSource,
                "NB0011",
                "Implement IStateReset");
    }
}
