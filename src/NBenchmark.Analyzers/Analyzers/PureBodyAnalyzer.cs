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
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor NoSideEffectRule = new(
        DiagnosticIds.NoObservableSideEffect,
        "[Benchmark] body has no observable side effects",
        "Method '{0}' has no observable side effects in its void body. The JIT may optimize it away. Call a method or consume the result via Benchmark.Run<T>.",
        "NBenchmark.Performance",
        DiagnosticSeverity.Info,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        [NoWorkRule, NoSideEffectRule];

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
                    context.ReportDiagnostic(Diagnostic.Create(NoSideEffectRule,
                        methodDecl.Identifier.GetLocation(), method.Name));
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