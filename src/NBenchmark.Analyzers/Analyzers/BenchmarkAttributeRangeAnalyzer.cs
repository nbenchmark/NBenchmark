using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BenchmarkAttributeRangeAnalyzer : DiagnosticAnalyzer
{
    private const int UnsetSentinel = -1;
    private const int MaxIterations = 100_000;
    private const int MaxWarmupIterations = 10_000;

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.BenchmarkAttributeRange,
        "[Benchmark] property value out of range",
        "{0}",
        "NBenchmark.Configuration",
        DiagnosticSeverity.Error,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not MethodDeclarationSyntax methodDecl)
            return;

        var method = context.SemanticModel.GetDeclaredSymbol(methodDecl) as IMethodSymbol;
        if (method is null)
            return;

        foreach (var attr in method.GetAttributes())
        {
            if (!BenchmarkSymbols.IsBenchmarkAttribute(attr.AttributeClass))
                continue;

            foreach (var namedArg in attr.NamedArguments)
            {
                switch (namedArg.Key)
                {
                    case "Iterations" when namedArg.Value.Value is int iters:
                        if (iters != UnsetSentinel && (iters < 0 || iters > MaxIterations))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Rule,
                                methodDecl.Identifier.GetLocation(),
                                $"[Benchmark(Iterations = {iters})] is out of range. Must be 0-{MaxIterations} (or -1 to use the suite default)."));
                        }
                        break;

                    case "WarmupIterations" when namedArg.Value.Value is int warmup:
                        if (warmup != UnsetSentinel && (warmup < 0 || warmup > MaxWarmupIterations))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Rule,
                                methodDecl.Identifier.GetLocation(),
                                $"[Benchmark(WarmupIterations = {warmup})] is out of range. Must be 0-{MaxWarmupIterations} (or -1 to use the suite default)."));
                        }
                        break;
                }
            }
        }
    }
}