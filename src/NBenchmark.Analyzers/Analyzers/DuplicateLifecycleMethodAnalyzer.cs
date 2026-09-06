using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateLifecycleMethodAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.DuplicateLifecycleMethod,
        "Duplicate lifecycle method in benchmark class",
        "Class '{0}' has multiple [{1}] methods. Only the first one found is used; '{2}' is ignored.",
        "NBenchmark.Usage",
        DiagnosticSeverity.Error,
        true);

    private static readonly string[] LifecycleAttributeNames =
    [
        "GlobalSetup",
        "GlobalTeardown",
        "SampleSetup",
        "SampleTeardown",
    ];

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

        var cls = context.SemanticModel.GetDeclaredSymbol(typeDecl);

        if (cls is null)
            return;

        if (!BenchmarkSymbols.HasDeclaredBenchmarkAttribute(cls))
            return;

        foreach (var attrName in LifecycleAttributeNames)
        {
            var fullName = $"NBenchmark.{attrName}Attribute";
            var methodsWithAttr = new List<IMethodSymbol>();

            foreach (var member in cls.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.ContainingType?.Equals(cls, SymbolEqualityComparer.Default) != true)
                    continue;

                foreach (var attr in member.GetAttributes())
                {
                    var attrFullName = attr.AttributeClass?.OriginalDefinition?.ToDisplayString()
                                       ?? attr.AttributeClass?.ToDisplayString();

                    if (attrFullName == fullName)
                    {
                        methodsWithAttr.Add(member);
                        break;
                    }
                }
            }

            if (methodsWithAttr.Count <= 1)
                continue;

            for (var i = 1; i < methodsWithAttr.Count; i++)
            {
                var methodDecl = methodsWithAttr[i].DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault();

                if (methodDecl is not null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule,
                        methodDecl.Identifier.GetLocation(),
                        cls.Name, attrName, methodsWithAttr[i].Name));
                }
            }
        }
    }
}
