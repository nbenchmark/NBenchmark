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
    private const int MaxSamplesLimit = 100_000;
    private const int MaxWarmupSamplesLimit = 10_000;

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

        var method = context.SemanticModel.GetDeclaredSymbol(methodDecl);

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
                    case "Samples" when namedArg.Value.Value is int iters:
                        if (iters != UnsetSentinel && (iters < 0 || iters > MaxSamplesLimit))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Rule,
                                methodDecl.Identifier.GetLocation(),
                                $"[Benchmark(Samples = {iters})] is out of range. Must be 0-{MaxSamplesLimit} (or -1 to use the suite default)."));
                        }

                        break;

                    case "WarmupSamples" when namedArg.Value.Value is int warmup:
                        if (warmup != UnsetSentinel && (warmup < 0 || warmup > MaxWarmupSamplesLimit))
                        {
                            context.ReportDiagnostic(Diagnostic.Create(Rule,
                                methodDecl.Identifier.GetLocation(),
                                $"[Benchmark(WarmupSamples = {warmup})] is out of range. Must be 0-{MaxWarmupSamplesLimit} (or -1 to use the suite default)."));
                        }

                        break;
                }
            }
        }
    }
}
