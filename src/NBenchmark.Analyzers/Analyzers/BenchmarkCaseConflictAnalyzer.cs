using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BenchmarkCaseConflictAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.BenchmarkCaseConflict,
        "[BenchmarkCases] cannot be combined with [BenchmarkCase]",
        "Method '{0}' has both [BenchmarkCases] and [BenchmarkCase]. Use one or the other.",
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

        var method = context.SemanticModel.GetDeclaredSymbol(methodDecl);

        if (method is null)
            return;

        if (!BenchmarkSymbols.HasBenchmarkAttribute(method))
            return;

        var hasCase = false;
        var hasCases = false;

        foreach (var attr in method.GetAttributes())
        {
            if (BenchmarkSymbols.IsBenchmarkCaseAttribute(attr.AttributeClass))
                hasCase = true;
            else if (BenchmarkSymbols.IsBenchmarkCasesAttribute(attr.AttributeClass))
                hasCases = true;
        }

        if (hasCase && hasCases)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                method.Name));
        }
    }
}
