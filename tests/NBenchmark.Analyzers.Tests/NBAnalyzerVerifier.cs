using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NBenchmark.Analyzers.Tests;

internal static class SharedReferences
{
    private static MetadataReference[]? _refs;
    private static readonly object _lock = new();

    public static MetadataReference[] Get()
    {
        if (_refs is not null)
            return _refs;

        lock (_lock)
        {
            if (_refs is not null)
                return _refs;

            var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var list = new List<MetadataReference>();

            foreach (var dll in Directory.EnumerateFiles(coreDir, "*.dll").OrderBy(x => x))
            {
                var name = Path.GetFileNameWithoutExtension(dll);

                if (name is "System.Private.Uri" or "System.Private.Xml")
                    continue;

                try
                {
                    list.Add(MetadataReference.CreateFromFile(dll));
                }
                catch
                {
                }
            }

            _refs = list.ToArray();
        }

        return _refs;
    }
}

internal static class TestSources
{
    public const string Stubs = """
                                namespace NBenchmark.Attributes
                                {
                                    [System.AttributeUsage(System.AttributeTargets.Method)]
                                    public sealed class BenchmarkAttribute : System.Attribute
                                    {
                                        public string? Description { get; set; }
                                        public bool Baseline { get; set; }
                                        public int Iterations { get; set; } = -1;
                                        public int WarmupIterations { get; set; } = -1;
                                    }
                                    [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
                                    public sealed class BenchmarkArgumentsAttribute : System.Attribute
                                    {
                                        public BenchmarkArgumentsAttribute(params object[] arguments) { Arguments = arguments; }
                                        public object[] Arguments { get; }
                                    }
                                    [System.AttributeUsage(System.AttributeTargets.Method)]
                                    public sealed class BenchmarkSetupAttribute : System.Attribute {}
                                    [System.AttributeUsage(System.AttributeTargets.Method)]
                                    public sealed class BenchmarkTeardownAttribute : System.Attribute {}
                                    [System.AttributeUsage(System.AttributeTargets.Method)]
                                    public sealed class BenchmarkIterationSetupAttribute : System.Attribute {}
                                    [System.AttributeUsage(System.AttributeTargets.Method)]
                                    public sealed class BenchmarkIterationTeardownAttribute : System.Attribute {}
                                    [System.AttributeUsage(System.AttributeTargets.Class)]
                                    public sealed class InstanceLifetimeAttribute : System.Attribute
                                    {
                                        public InstanceLifetimeAttribute(NBenchmark.InstanceLifetime lifetime) {}
                                    }
                                }
                                namespace NBenchmark
                                {
                                    public enum InstanceLifetime
                                    {
                                        PerMethod = 0,
                                        PerClass = 1,
                                    }
                                    public sealed class BenchmarkResult {}
                                    public sealed class MeasurementOutcome {}

                                    public sealed class MeasurementOptions
                                    {
                                        public int Iterations { get; init; }
                                        public int WarmupIterations { get; init; }
                                        public double ConfidenceLevel { get; init; }
                                    }
                                    public static class Benchmark
                                    {
                                        public static BenchmarkResult Run(System.Action action) { return new BenchmarkResult(); }
                                        public static BenchmarkResult Run<T>(System.Func<T> action) { return new BenchmarkResult(); }
                                        public static System.Threading.Tasks.Task<BenchmarkResult> RunAsync(System.Func<System.Threading.Tasks.Task> action) { return System.Threading.Tasks.Task.FromResult(new BenchmarkResult()); }
                                        public static System.Threading.Tasks.Task<BenchmarkResult> RunAsync<T>(System.Func<System.Threading.Tasks.Task<T>> action) { return System.Threading.Tasks.Task.FromResult(new BenchmarkResult()); }

                                        public static MeasurementOutcome RunRaw(System.Action action) { return new MeasurementOutcome(); }
                                        public static MeasurementOutcome RunRaw<T>(System.Func<T> action) { return new MeasurementOutcome(); }
                                        public static System.Threading.Tasks.Task<MeasurementOutcome> RunRawAsync(System.Func<System.Threading.Tasks.Task> action) { return System.Threading.Tasks.Task.FromResult(new MeasurementOutcome()); }
                                        public static System.Threading.Tasks.Task<MeasurementOutcome> RunRawAsync<T>(System.Func<System.Threading.Tasks.Task<T>> action) { return System.Threading.Tasks.Task.FromResult(new MeasurementOutcome()); }
                                    }
                                }
                                """;
}

public static class NBAnalyzerVerifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    private static readonly TAnalyzer Analyzer = new();

    public static async Task VerifyAnalyzerAsync(string source, string diagnosticId)
    {
        var analyzerDiagnostics = await GetDiagnosticsAsync(source);
        var msg = string.Join("\n", analyzerDiagnostics.Select(d => $"  [{d.Id}] {d.GetMessage()}"));
        Assert.True(analyzerDiagnostics.Length > 0,
            $"Expected diagnostic '{diagnosticId}' but found none.\nAll diagnostics: {msg}");
        Assert.Contains(analyzerDiagnostics, d => d.Id == diagnosticId);
    }

    public static async Task VerifyNoDiagnosticAsync(string source)
    {
        var analyzerDiagnostics = await GetDiagnosticsAsync(source);

        Assert.True(analyzerDiagnostics.Length == 0,
            $"Expected no diagnostics but found:\n{string.Join("\n", analyzerDiagnostics.Select(d => $"  [{d.Id}] {d.GetMessage()}"))}");
    }

    public static async Task VerifyNoDiagnosticAsync(string source, string diagnosticId)
    {
        var analyzerDiagnostics = await GetDiagnosticsAsync(source);
        Assert.DoesNotContain(analyzerDiagnostics, d => d.Id == diagnosticId);
    }

    public static async Task VerifyCodeFixAsync<TCodeFix>(string source, string fixedSource, string diagnosticId)
        where TCodeFix : CodeFixProvider, new()
    {
        var analyzerDiagnostics = await GetDiagnosticsAsync(source);
        Assert.True(analyzerDiagnostics.Length > 0, $"Expected diagnostic '{diagnosticId}' but found none.");

        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var documentId = DocumentId.CreateNewId(projectId);
        var stubDocumentId = DocumentId.CreateNewId(projectId);
        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12);
        var compilationOptions = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

        var solution = workspace.CurrentSolution
            .AddProject(projectId, "TestAssembly", "TestAssembly", LanguageNames.CSharp)
            .AddMetadataReferences(projectId, SharedReferences.Get())
            .WithProjectCompilationOptions(projectId, compilationOptions)
            .WithProjectParseOptions(projectId, parseOptions)
            .AddDocument(documentId, "Test.cs", source)
            .AddDocument(stubDocumentId, "Stubs.cs", TestSources.Stubs);

        var project = solution.GetProject(projectId)!
            .AddAnalyzerReference(new AnalyzerImageReference(ImmutableArray.Create<DiagnosticAnalyzer>(new TAnalyzer())));

        var document = project.GetDocument(documentId)!;

        var codeFixProvider = new TCodeFix();
        var actions = new List<CodeAction>();
        var context = new CodeFixContext(document, analyzerDiagnostics[0], (action, _) => actions.Add(action), CancellationToken.None);
        await codeFixProvider.RegisterCodeFixesAsync(context);

        Assert.True(actions.Count > 0, "Expected at least one code fix action.");
        var applied = await actions[0].GetOperationsAsync(CancellationToken.None);
        var changedSolution = applied.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
        Assert.NotNull(changedSolution);
        var changedDoc = changedSolution!.GetDocument(documentId)!;
        var changedText = (await changedDoc.GetTextAsync()).ToString().Trim();

        var expectedTree = CSharpSyntaxTree.ParseText(fixedSource, parseOptions);
        var actualTree = CSharpSyntaxTree.ParseText(changedText, parseOptions);

        Assert.True(actualTree.IsEquivalentTo(expectedTree),
            $"Code fix output does not match expected.\nExpected:\n{fixedSource}\n\nActual:\n{changedText}");
    }

    private static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
    {
        var userTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12));
        var stubTree = CSharpSyntaxTree.ParseText(TestSources.Stubs, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12));

        var compilation = CSharpCompilation.Create("TestAssembly", [userTree, stubTree], SharedReferences.Get(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var compilationWithAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(Analyzer));
        var allDiagnostics = await compilationWithAnalyzers.GetAllDiagnosticsAsync().ConfigureAwait(false);
        var supportedIds = Analyzer.SupportedDiagnostics.Select(d => d.Id).ToImmutableHashSet();
        return allDiagnostics.Where(d => supportedIds.Contains(d.Id)).ToImmutableArray();
    }
}
