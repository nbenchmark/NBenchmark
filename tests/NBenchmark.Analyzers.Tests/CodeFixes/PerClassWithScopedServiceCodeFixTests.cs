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
                                  return System.Threading.Tasks.Task.CompletedTask;
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
