using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BenchmarkArgumentsArityAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.BenchmarkArgumentsArity,
        "[BenchmarkArguments] must match method parameters",
        "{0}",
        "NBenchmark.Usage",
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

        if (!BenchmarkSymbols.HasBenchmarkAttribute(method))
            return;

        var parameterCount = method.Parameters.Length;
        var argumentSets = GetBenchmarkArgumentsAttributes(method);

        if (parameterCount == 0 && argumentSets.Length > 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has [BenchmarkArguments] but takes no parameters."));
            return;
        }

        if (parameterCount > 0 && argumentSets.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has {parameterCount} parameter(s) but no [BenchmarkArguments]. Add one [BenchmarkArguments(...)] per argument set."));
            return;
        }

        foreach (var attr in argumentSets)
        {
            var effectiveCount = GetEffectiveArgumentCount(attr);
            if (effectiveCount == 0 && parameterCount > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule,
                    methodDecl.Identifier.GetLocation(),
                    $"Method '{method.Name}' expects {parameterCount} argument(s) but a [BenchmarkArguments] attribute supplies none."));
            }
            else if (effectiveCount > 0 && effectiveCount != parameterCount)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule,
                    methodDecl.Identifier.GetLocation(),
                    $"Method '{method.Name}' expects {parameterCount} argument(s) but a [BenchmarkArguments] attribute supplies {effectiveCount}."));
            }
        }
    }

    private static int GetEffectiveArgumentCount(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 0)
            return 0;

        if (attr.ConstructorArguments.Length == 1 &&
            attr.ConstructorArguments[0].Kind == TypedConstantKind.Array)
        {
            return attr.ConstructorArguments[0].Values.Length;
        }

        return attr.ConstructorArguments.Length;
    }

    private static ImmutableArray<AttributeData> GetBenchmarkArgumentsAttributes(IMethodSymbol method)
    {
        var builder = ImmutableArray.CreateBuilder<AttributeData>();
        foreach (var attr in method.GetAttributes())
        {
            if (BenchmarkSymbols.IsBenchmarkArgumentsAttribute(attr.AttributeClass))
                builder.Add(attr);
        }
        return builder.ToImmutable();
    }
}