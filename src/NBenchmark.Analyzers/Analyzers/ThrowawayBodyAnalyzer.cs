using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ThrowawayBodyAnalyzer : DiagnosticAnalyzer
{
    private const string BenchmarkClassName = "NBenchmark.Benchmark";

    private static readonly ImmutableHashSet<string> TargetMethodNames =
        ImmutableHashSet.Create("Run", "RunRaw");

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.ThrowawayBody,
        "Benchmark body appears to be throwaway",
        "The lambda body passed to Benchmark.{0} has no observable side effects. Use Benchmark.Run<T>(() => value) to consume the result, or add a side effect.",
        "NBenchmark.Performance",
        DiagnosticSeverity.Warning,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not InvocationExpressionSyntax invocation)
            return;

        if (invocation.ArgumentList.Arguments.Count < 1)
            return;

        var firstArg = invocation.ArgumentList.Arguments[0].Expression;

        if (firstArg is not LambdaExpressionSyntax lambda)
            return;

        var methodSymbol = context.SemanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
        if (methodSymbol is null)
            return;

        if (methodSymbol.ContainingType?.ToDisplayString() != BenchmarkClassName)
            return;

        if (!TargetMethodNames.Contains(methodSymbol.Name))
            return;

        if (methodSymbol.Parameters.Length == 0)
            return;

        if (methodSymbol.Parameters[0].Type is not INamedTypeSymbol delegateType)
            return;

        var invokeMethod = delegateType.DelegateInvokeMethod;
        if (invokeMethod is null || !invokeMethod.ReturnsVoid)
            return;

        var walker = new SideEffectWalker(context.SemanticModel);
        walker.Visit(lambda.Body);

        if (!walker.HasAnyEffect)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                invocation.GetLocation(), methodSymbol.Name));
        }
    }
}