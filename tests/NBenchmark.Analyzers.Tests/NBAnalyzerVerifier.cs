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
                                    namespace NBenchmark
                                    {
                                        [System.AttributeUsage(System.AttributeTargets.Method)]
                                        public sealed class BenchmarkAttribute : System.Attribute
                                        {
                                            public string? Description { get; set; }
                                            public bool Baseline { get; set; }
                                            public int Samples { get; set; } = -1;
                                            public int WarmupSamples { get; set; } = -1;
                                        }
                                        [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
                                        public sealed class ArgumentsAttribute : System.Attribute
                                        {
                                            public ArgumentsAttribute(params object[] arguments) { Arguments = arguments; }
                                            public object[] Arguments { get; }
                                        }
                                        [System.AttributeUsage(System.AttributeTargets.Method)]
                                        public sealed class ArgumentsSourceAttribute : System.Attribute
                                        {
                                            public ArgumentsSourceAttribute(string sourceName) { SourceName = sourceName; }
                                            public string SourceName { get; }
                                        }
                                        [System.AttributeUsage(System.AttributeTargets.Method)]
                                        public sealed class GlobalSetupAttribute : System.Attribute {}
                                        [System.AttributeUsage(System.AttributeTargets.Method)]
                                        public sealed class GlobalTeardownAttribute : System.Attribute {}
                                        [System.AttributeUsage(System.AttributeTargets.Method)]
                                        public sealed class SampleSetupAttribute : System.Attribute {}
                                        [System.AttributeUsage(System.AttributeTargets.Method)]
                                        public sealed class SampleTeardownAttribute : System.Attribute {}
                                        [System.AttributeUsage(System.AttributeTargets.Class)]
                                        public sealed class InstanceLifetimeAttribute : System.Attribute
                                        {
                                            public InstanceLifetimeAttribute(NBenchmark.InstanceLifetime lifetime) {}
                                        }
                                        [System.AttributeUsage(System.AttributeTargets.Class)]
                                        public sealed class SharedStateAttribute : System.Attribute
                                        {
                                            public bool Acknowledged { get; init; } = true;
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
                                            public int Samples { get; init; }
                                            public int WarmupSamples { get; init; }
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

                                    /// Mirrors the real Add signatures closely enough to matter to NB0014: the body is the
                                    /// second parameter and is followed by optional setup/teardown delegates, so an analyzer
                                    /// that found the body by argument position would report the wrong lambda here.
                                    public sealed class BenchmarkSuite
                                    {
                                        public BenchmarkSuite(string name) {}

                                        public BenchmarkSuite Add(string name, System.Action action,
                                            System.Action? setup = null, System.Action? teardown = null,
                                            System.Collections.Generic.IReadOnlyList<string>? categories = null) { return this; }

                                        public BenchmarkSuite Add(string name, System.Func<System.Threading.Tasks.Task> action,
                                            System.Action? setup = null, System.Action? teardown = null,
                                            System.Collections.Generic.IReadOnlyList<string>? categories = null) { return this; }

                                        public BenchmarkSuite Add<T>(string name, System.Func<T> action,
                                            System.Action? setup = null, System.Action? teardown = null,
                                            System.Collections.Generic.IReadOnlyList<string>? categories = null) { return this; }

                                        public BenchmarkSuite Add<T>(string name, System.Func<System.Threading.Tasks.Task<T>> action,
                                            System.Action? setup = null, System.Action? teardown = null,
                                            System.Collections.Generic.IReadOnlyList<string>? categories = null) { return this; }

                                        public BenchmarkSuite Add<T>(string name, System.Action<T> action,
                                            System.Action? setup = null, System.Action? teardown = null,
                                            System.Collections.Generic.IReadOnlyList<string>? categories = null) { return this; }

                                        public BenchmarkSuite WithParameter<T>(string name, params T[] values) { return this; }

                                        public System.Threading.Tasks.Task<BenchmarkResult> RunAsync() { return System.Threading.Tasks.Task.FromResult(new BenchmarkResult()); }
                                    }

                                    /// The fluent instance-lifetime default is a whole-compilation fact - it applies to
                                    /// every discovered class in the assembly - so NB0011 has to see the call to know
                                    /// what a class carrying no [InstanceLifetime] attribute actually runs as.
                                    public sealed class BenchmarkHarness
                                    {
                                        public BenchmarkHarness WithInstanceLifetime(NBenchmark.InstanceLifetime lifetime) { return this; }
                                    }
                                }
                                    namespace NBenchmark.Lifecycle
                                    {
                                        public interface IStateReset
                                        {
                                            System.Threading.Tasks.Task ResetAsync(System.Threading.CancellationToken cancellationToken);
                                        }
                                    }
                                """;
}

public static class NBAnalyzerVerifier<TAnalyzer>
    where TAnalyzer : DiagnosticAnalyzer, new()
{
    private static readonly TAnalyzer Analyzer = new();

    public static async Task VerifyAnalyzerAsync(string source, string diagnosticId, DiagnosticSeverity? expectedSeverity = null)
    {
        var analyzerDiagnostics = await GetDiagnosticsAsync(source);
        var msg = string.Join("\n", analyzerDiagnostics.Select(d => $"  [{d.Id}] {d.GetMessage()} ({d.Severity})"));

        Assert.True(analyzerDiagnostics.Length > 0,
            $"Expected diagnostic '{diagnosticId}' but found none.\nAll diagnostics: {msg}");

        var match = analyzerDiagnostics.FirstOrDefault(d => d.Id == diagnosticId);
        Assert.True(match is { Id: not null }, $"Expected diagnostic '{diagnosticId}' but found:\n{msg}");

        if (expectedSeverity.HasValue)
            Assert.Equal(expectedSeverity.Value, match.Severity);
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

    public static async Task VerifyCodeFixAsync<TCodeFix>(
        string source,
        string fixedSource,
        string diagnosticId,
        string? preferredCodeActionTitle = null)
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

        var selectedAction = string.IsNullOrWhiteSpace(preferredCodeActionTitle)
            ? actions[0]
            : actions.FirstOrDefault(a => string.Equals(a.Title, preferredCodeActionTitle, StringComparison.Ordinal));

        Assert.NotNull(selectedAction);

        var applied = await selectedAction!.GetOperationsAsync(CancellationToken.None);
        var changedSolution = applied.OfType<ApplyChangesOperation>().FirstOrDefault()?.ChangedSolution;
        Assert.NotNull(changedSolution);
        var changedDoc = changedSolution!.GetDocument(documentId)!;
        var changedText = (await changedDoc.GetTextAsync()).ToString().Trim();

        var expectedTree = CSharpSyntaxTree.ParseText(fixedSource, parseOptions);
        var actualTree = CSharpSyntaxTree.ParseText(changedText, parseOptions);

        Assert.True(actualTree.IsEquivalentTo(expectedTree),
            $"Code fix output does not match expected.\nExpected:\n{fixedSource}\n\nActual:\n{changedText}");
    }

    /// <summary>
    ///     Every diagnostic the analyzer produced, compiling against the real NBenchmark assembly
    ///     instead of <see cref="TestSources.Stubs" />.
    /// </summary>
    /// <remarks>
    ///     The stub is a convenience, not evidence: a rule that matches on a symbol's shape - a
    ///     containing type, a method name, a parameter name - can stop matching the shipped API while
    ///     every stub test stays green, because the stub is the thing the tests also own.
    /// </remarks>
    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAgainstNBenchmarkAsync(string source)
    {
        var userTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp12));

        var references = SharedReferences.Get()
            .Append(MetadataReference.CreateFromFile(typeof(NBenchmark.BenchmarkSuite).Assembly.Location))
            .ToArray();

        var compilation = CSharpCompilation.Create("TestAssembly", [userTree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // A source that failed to bind would produce no diagnostics and read as a passing negative
        // test, so the compile is checked before the analyzer's answer is trusted.
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();

        Assert.True(errors.Length == 0,
            $"Test source did not compile against NBenchmark:\n{string.Join("\n", errors.Select(d => d.ToString()))}");

        var compilationWithAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(Analyzer));
        var analyzerDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync().ConfigureAwait(false);
        var supportedIds = Analyzer.SupportedDiagnostics.Select(d => d.Id).ToImmutableHashSet();
        return analyzerDiagnostics.Where(d => supportedIds.Contains(d.Id)).ToImmutableArray();
    }

    /// <summary>
    ///     Every diagnostic the analyzer produced. Exposed so a test can assert on a message rather
    ///     than only on an id - a rule whose job is to name symbols is only half-tested by knowing it
    ///     fired.
    /// </summary>
    public static async Task<ImmutableArray<Diagnostic>> GetDiagnosticsAsync(string source)
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
