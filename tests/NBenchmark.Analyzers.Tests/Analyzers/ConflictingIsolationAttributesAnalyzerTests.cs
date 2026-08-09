using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

/// <summary>
///     NB0015 - one member asking for both the host process and a dedicated worker.
/// </summary>
public sealed class ConflictingIsolationAttributesAnalyzerTests
{
    [Fact]
    public async Task Reports_both_attributes_on_one_method()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark, InProcess, IsolatedProcess] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<ConflictingIsolationAttributesAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0015");
    }

    [Fact]
    public async Task Reports_both_attributes_on_one_class()
    {
        var code = """
                   using NBenchmark.Attributes;
                   [InProcess]
                   [IsolatedProcess]
                   public class C {
                       [Benchmark] public void M() { }
                   }
                   """;

        await NBAnalyzerVerifier<ConflictingIsolationAttributesAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0015");
    }

    /// <summary>
    ///     A method-level attribute overriding a class-level one is the documented way to force one
    ///     benchmark out of a mostly-in-process class. Reporting it would break the shape both
    ///     attributes exist to support, so the rule is deliberately about a single member.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_when_the_method_overrides_the_class()
    {
        var code = """
                   using NBenchmark.Attributes;
                   [InProcess]
                   public class C {
                       [Benchmark, IsolatedProcess] public void M() { }
                       [Benchmark] public void N() { }
                   }
                   """;

        await NBAnalyzerVerifier<ConflictingIsolationAttributesAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0015");
    }

    [Fact]
    public async Task No_diagnostic_for_either_attribute_alone()
    {
        var code = """
                   using NBenchmark.Attributes;
                   public class C {
                       [Benchmark, InProcess] public void M() { }
                       [Benchmark, IsolatedProcess] public void N() { }
                   }
                   """;

        await NBAnalyzerVerifier<ConflictingIsolationAttributesAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0015");
    }
}
