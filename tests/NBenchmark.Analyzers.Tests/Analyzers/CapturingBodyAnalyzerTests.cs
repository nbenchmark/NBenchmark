using NBenchmark.Analyzers.Analyzers;

namespace NBenchmark.Analyzers.Tests.Analyzers;

/// <summary>
///     NB0014 tells a developer, before the run, that a body will not be isolated. The tests that
///     matter most here are the negative ones: a diagnostic on every lambda would be noise, and noise
///     on an informational rule is indistinguishable from the rule being off.
/// </summary>
public sealed class CapturingBodyAnalyzerTests
{
    [Fact]
    public async Task Reports_a_captured_local()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           Benchmark.Run(() => System.Array.Sort(data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    [Fact]
    public async Task Reports_a_captured_parameter()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M(int n) {
                           Benchmark.Run(() => System.Math.Sqrt(n));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    /// <summary>
    ///     The case a developer is least likely to spot: no identifier in the body looks like it came
    ///     from outside, but naming an instance member without a receiver captures the whole object.
    /// </summary>
    [Fact]
    public async Task Reports_an_implicit_this_capture_through_a_field()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       private int[] _data = new int[100];
                       public void M() {
                           Benchmark.Run(() => System.Array.Sort(_data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    [Fact]
    public async Task Reports_an_explicit_this_capture()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       private int _n;
                       public void M() {
                           Benchmark.Run(() => System.Math.Sqrt(this._n));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    [Fact]
    public async Task Reports_an_instance_method_call_without_a_receiver()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       private int Work() { return 1; }
                       public void M() {
                           Benchmark.Run(() => Work());
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    [Fact]
    public async Task No_diagnostic_for_a_self_contained_body()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.Run(() => { var data = new int[100]; System.Array.Sort(data); });
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    /// <summary>
    ///     A static member needs no receiver, so naming one is not a capture. Roslyn still lowers this
    ///     lambda to an instance method on a cached singleton - which is exactly why the runtime
    ///     cannot use <c>Delegate.Target is null</c> as its test - but there is no state to carry.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_for_a_static_member_reference()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       private static readonly int[] Data = new int[100];
                       public void M() {
                           Benchmark.Run(() => System.Array.Sort(Data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    [Fact]
    public async Task No_diagnostic_for_a_constant_body()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.Run(() => 43);
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    [Fact]
    public async Task No_diagnostic_for_an_explicitly_static_lambda()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.Run(static () => 43);
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    /// <summary>
    ///     The lambda's own parameters are supplied at each call, not carried from anywhere.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_when_a_nested_lambda_closes_over_the_outer_parameter()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           Benchmark.Run(() => {
                               var xs = new int[10];
                               System.Array.ForEach(xs, x => { var y = x + 1; });
                           });
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    [Fact]
    public async Task No_diagnostic_for_a_lambda_passed_to_something_else()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           System.Action a = () => System.Array.Sort(data);
                           a();
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    [Fact]
    public async Task Reports_on_every_entry_point_that_takes_a_body()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public async System.Threading.Tasks.Task M() {
                           var data = new int[100];
                           Benchmark.Run(() => System.Array.Sort(data));
                           await Benchmark.RunAsync(async () => { await System.Threading.Tasks.Task.Yield(); System.Array.Sort(data); });
                           Benchmark.RunRaw(() => System.Array.Sort(data));
                           await Benchmark.RunRawAsync(async () => { await System.Threading.Tasks.Task.Yield(); System.Array.Sort(data); });
                       }
                   }
                   """;

        var diagnostics = await NBAnalyzerVerifier<CapturingBodyAnalyzer>.GetDiagnosticsAsync(code);

        Assert.Equal(4, diagnostics.Count(d => d.Id == "NB0014"));
    }

    /// <summary>
    ///     The reason this rule exists rather than leaving it to the runtime label: it can say which
    ///     symbols are responsible. At runtime they are fields on a compiler-generated class.
    /// </summary>
    [Fact]
    public async Task The_message_names_every_captured_symbol()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M(int seed) {
                           var data = new int[100];
                           Benchmark.Run(() => { data[0] = seed; System.Array.Sort(data); });
                       }
                   }
                   """;

        var diagnostics = await NBAnalyzerVerifier<CapturingBodyAnalyzer>.GetDiagnosticsAsync(code);
        var message = Assert.Single(diagnostics.Where(d => d.Id == "NB0014")).GetMessage();

        Assert.Contains("'data'", message);
        Assert.Contains("'seed'", message);
    }

    [Fact]
    public async Task The_rule_is_informational_so_an_idiomatic_benchmark_does_not_break_a_build()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           Benchmark.Run(() => System.Array.Sort(data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>
            .VerifyAnalyzerAsync(code, "NB0014", Microsoft.CodeAnalysis.DiagnosticSeverity.Info);
    }
}
