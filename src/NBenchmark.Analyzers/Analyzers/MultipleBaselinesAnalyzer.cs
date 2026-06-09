using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MultipleBaselinesAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.MultipleBaselines,
        "Only one [Benchmark(Baseline = true)] allowed per class",
        "Class '{0}' has multiple [Benchmark] methods with Baseline = true. Only the first found baseline is used; others are ignored.",
        "NBenchmark.Configuration",
        DiagnosticSeverity.Error,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.ClassDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeType, SyntaxKind.RecordDeclaration);
    }

    private static void AnalyzeType(SyntaxNodeAnalysisContext context)
    {
        TypeDeclarationSyntax? typeDecl = context.Node switch
        {
            ClassDeclarationSyntax c => c,
            RecordDeclarationSyntax r => r,
            _ => null,
        };

        if (typeDecl is null)
            return;

        var cls = context.SemanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
        if (cls is null)
            return;

        if (!BenchmarkSymbols.HasDeclaredBenchmarkAttribute(cls))
            return;

        string? firstBaselineName = null;
        foreach (var member in cls.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.ContainingType?.Equals(cls, SymbolEqualityComparer.Default) != true)
                continue;
            foreach (var attr in member.GetAttributes())
            {
                if (!BenchmarkSymbols.IsBenchmarkAttribute(attr.AttributeClass))
                    continue;

                var isBaseline = false;
                foreach (var namedArg in attr.NamedArguments)
                {
                    if (namedArg.Key == "Baseline" && namedArg.Value.Value is true)
                    {
                        isBaseline = true;
                        break;
                    }
                }

                if (!isBaseline)
                    continue;

                if (firstBaselineName is null)
                {
                    firstBaselineName = member.Name;
                }
                else
                {
                    var methodDecl = member.DeclaringSyntaxReferences
                        .Select(r => r.GetSyntax())
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault();

                    if (methodDecl is not null)
                    {
                        context.ReportDiagnostic(Diagnostic.Create(Rule,
                            methodDecl.Identifier.GetLocation(), cls.Name));
                    }
                }
            }
        }
    }
}
