using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PureBodyAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor NoWorkRule = new(
        DiagnosticIds.NoWorkBody,
        "[Benchmark] body does no observable work",
        "Method '{0}' body does no observable work. The JIT may eliminate it entirely, producing 0 ns results.",
        "NBenchmark.Performance",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor NoSideEffectRule = new(
        DiagnosticIds.NoObservableSideEffect,
        "[Benchmark] body has no observable side effects",
        "Method '{0}' has no observable side effects in its void body. The JIT may optimize it away. Return a value, call a side-effecting method, or suppress with #pragma warning disable NBenchmark.NB0004 if the analyzer cannot see the work.",
        "NBenchmark.Performance",
        DiagnosticSeverity.Error,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(NoWorkRule, NoSideEffectRule);

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

        if (!method.ReturnsVoid)
            return;

        if (!BenchmarkSymbols.HasBenchmarkAttribute(method))
            return;

        if (methodDecl.Body is null)
        {
            if (methodDecl.ExpressionBody is not null)
            {
                var walker = new SideEffectWalker(context.SemanticModel);
                walker.Visit(methodDecl.ExpressionBody.Expression);

                if (!walker.HasAnyEffect)
                {
                    context.ReportDiagnostic(Diagnostic.Create(NoSideEffectRule,
                        methodDecl.Identifier.GetLocation(), method.Name));
                }
            }

            return;
        }

        var statements = methodDecl.Body.Statements;

        if (statements.Count == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(NoWorkRule,
                methodDecl.Identifier.GetLocation(), method.Name));

            return;
        }

        var sideEffectWalker = new SideEffectWalker(context.SemanticModel);
        sideEffectWalker.Visit(methodDecl.Body);

        if (!sideEffectWalker.HasAnyEffect)
        {
            context.ReportDiagnostic(Diagnostic.Create(NoSideEffectRule,
                methodDecl.Identifier.GetLocation(), method.Name));
        }
    }
}
