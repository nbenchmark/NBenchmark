using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MissingParameterlessConstructorAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.MissingParameterlessConstructor,
        "Benchmark class must have a public parameterless constructor",
        "Type '{0}' has [Benchmark] methods but no public parameterless constructor. Add one or use NBenchmark.DependencyInjection.",
        "NBenchmark.Usage",
        DiagnosticSeverity.Warning,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

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

        if (typeDecl.Modifiers.Any(SyntaxKind.AbstractKeyword))
            return;

        var type = context.SemanticModel.GetDeclaredSymbol(typeDecl);

        if (type is null)
            return;

        if (!BenchmarkSymbols.HasDeclaredBenchmarkAttribute(type))
            return;

        if (type.IsValueType)
            return;

        if (HasPublicParameterlessConstructor(type))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, typeDecl.Identifier.GetLocation(), type.Name));
    }

    private static bool HasPublicParameterlessConstructor(INamedTypeSymbol type)
    {
        foreach (var ctor in type.Constructors)
        {
            if (!ctor.IsStatic && ctor.DeclaredAccessibility == Accessibility.Public && ctor.Parameters.Length == 0)
                return true;
        }

        return false;
    }
}
