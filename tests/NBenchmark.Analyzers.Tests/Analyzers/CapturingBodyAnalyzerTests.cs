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

    /// <summary>
    ///     <c>Add</c> is the larger half of the surface, and the one where capture reads as idiomatic.
    /// </summary>
    [Fact]
    public async Task Reports_a_captured_local_in_a_suite_body()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           new BenchmarkSuite("s").Add("Sort", () => System.Array.Sort(data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    [Fact]
    public async Task Reports_an_implicit_this_capture_in_a_suite_body()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       private int[] _data = new int[100];
                       public void M() {
                           new BenchmarkSuite("s").Add("Sort", () => System.Array.Sort(_data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    /// <summary>
    ///     One diagnostic per capturing body, and nothing on the self-contained sibling - which is the
    ///     half of a suite the author can leave alone.
    /// </summary>
    [Fact]
    public async Task Reports_each_capturing_body_in_a_fluent_chain()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public async System.Threading.Tasks.Task M() {
                           var data = new int[100];
                           await new BenchmarkSuite("s")
                               .Add("A", () => System.Array.Sort(data))
                               .Add("B", () => { var own = new int[100]; System.Array.Sort(own); })
                               .Add("C", () => data.Length)
                               .Add("D", async () => { await System.Threading.Tasks.Task.Yield(); System.Array.Sort(data); })
                               .RunAsync();
                       }
                   }
                   """;

        var diagnostics = await NBAnalyzerVerifier<CapturingBodyAnalyzer>.GetDiagnosticsAsync(code);

        Assert.Equal(3, diagnostics.Count(d => d.Id == "NB0014"));
    }

    /// <summary>
    ///     The message has to say that the whole suite falls back, because that is what
    ///     <c>InlineSuitePlan</c> does with the first body it cannot address.
    /// </summary>
    [Fact]
    public async Task The_suite_message_states_that_the_whole_suite_falls_back()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           new BenchmarkSuite("s").Add("Sort", () => System.Array.Sort(data));
                       }
                   }
                   """;

        var diagnostics = await NBAnalyzerVerifier<CapturingBodyAnalyzer>.GetDiagnosticsAsync(code);
        var message = Assert.Single(diagnostics.Where(d => d.Id == "NB0014")).GetMessage();

        Assert.Contains("BenchmarkSuite.Add", message);
        Assert.Contains("whole suite", message);
        Assert.Contains("'data'", message);
    }

    [Fact]
    public async Task No_diagnostic_for_a_self_contained_suite_body()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           new BenchmarkSuite("s").Add("Sort", () => { var data = new int[100]; System.Array.Sort(data); });
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    /// <summary>
    ///     The body is found by parameter, not by position: <c>Add</c> takes the name first and also
    ///     accepts <c>setup</c> and <c>teardown</c> delegates. A capturing setup delegate is not a
    ///     measured body, and the suite is refused isolation for having lifecycle at all - a capture
    ///     diagnostic on it would name a cause that is not the operative one.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_for_a_capturing_setup_delegate()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           new BenchmarkSuite("s").Add("Sort",
                               () => { var own = new int[100]; System.Array.Sort(own); },
                               setup: () => System.Array.Clear(data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    [Fact]
    public async Task Reports_a_body_passed_as_a_named_argument()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           new BenchmarkSuite("s").Add(name: "Sort", action: () => System.Array.Sort(data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyAnalyzerAsync(code, "NB0014");
    }

    /// <summary>
    ///     A parameterized suite is refused isolation for its parameter values, which exist only in
    ///     this process, whether or not any body captures. Reporting a capture here would point at
    ///     something whose removal would not restore isolation.
    /// </summary>
    [Fact]
    public async Task No_diagnostic_for_a_parameterized_suite_body()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           new BenchmarkSuite("s")
                               .WithParameter("n", 1, 2)
                               .Add<int>("Sort", n => System.Array.Sort(data, 0, n));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
    }

    /// <summary>
    ///     Compiled against the real assembly, not the stub. The rule locates the body by the
    ///     parameter's name - <c>action</c>, on all sixteen <c>Add</c> overloads and on the
    ///     <c>Run</c> family - and a rename there would silence it with every stub test still green.
    ///     This also pins the two shapes together: one suite body and one <c>Benchmark.Run</c> body,
    ///     each reported, against the API as shipped.
    /// </summary>
    [Fact]
    public async Task Reports_against_the_real_NBenchmark_api()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public async System.Threading.Tasks.Task M() {
                           var data = new int[100];
                           Benchmark.Run(() => System.Array.Sort(data));
                           await new BenchmarkSuite("s")
                               .Add("Sort", () => System.Array.Sort(data))
                               .Add("Own", () => { var own = new int[100]; System.Array.Sort(own); })
                               .RunAsync();
                       }
                   }
                   """;

        var diagnostics = await NBAnalyzerVerifier<CapturingBodyAnalyzer>.GetDiagnosticsAgainstNBenchmarkAsync(code);
        var messages = diagnostics.Where(d => d.Id == "NB0014").Select(d => d.GetMessage()).ToList();

        Assert.Equal(2, messages.Count);
        Assert.Contains(messages, m => m.Contains("Benchmark.Run") && m.Contains("'data'"));
        Assert.Contains(messages, m => m.Contains("BenchmarkSuite.Add") && m.Contains("whole suite"));
    }

    [Fact]
    public async Task No_diagnostic_for_an_Add_on_something_else()
    {
        var code = """
                   using NBenchmark;
                   public class C {
                       public void M() {
                           var data = new int[100];
                           var actions = new System.Collections.Generic.List<System.Action>();
                           actions.Add(() => System.Array.Sort(data));
                       }
                   }
                   """;

        await NBAnalyzerVerifier<CapturingBodyAnalyzer>.VerifyNoDiagnosticAsync(code, "NB0014");
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
