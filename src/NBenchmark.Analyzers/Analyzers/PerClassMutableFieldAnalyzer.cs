using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PerClassMutableFieldAnalyzer : DiagnosticAnalyzer
{
    private const string InstanceLifetimeTypeMetadataName = "NBenchmark.InstanceLifetime";
    private const string PerClassMemberName = "PerClass";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.PerClassMutableField,
        "PerClass instance lifetime with mutable instance field may cause state contamination",
        "Class '{0}' uses [InstanceLifetime(PerClass)] and has mutable instance field '{1}' that is accessed by multiple [Benchmark] methods. Sharing a single instance across methods can cause the second method to observe cached state from the first. Consider [InstanceLifetime(PerMethod)] or making the field readonly.",
        "NBenchmark.Usage",
        DiagnosticSeverity.Warning,
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

        if (typeDecl.Modifiers.Any(SyntaxKind.AbstractKeyword))
            return;

        var type = context.SemanticModel.GetDeclaredSymbol(typeDecl);

        if (type is null)
            return;

        if (!BenchmarkSymbols.HasDeclaredBenchmarkAttribute(type))
            return;

        if (!HasPerClassLifetime(context.Compilation, type))
            return;

        var mutableFields = GetMutableInstanceFields(type);

        if (mutableFields.Count == 0)
            return;

        var benchmarkMethods = type.GetMembers().OfType<IMethodSymbol>()
            .Where(m => BenchmarkSymbols.HasBenchmarkAttribute(m))
            .ToList();

        if (benchmarkMethods.Count < 2)
            return;

        foreach (var field in mutableFields)
        {
            var accessingMethods = new List<IMethodSymbol>();

            foreach (var method in benchmarkMethods)
            {
                if (MethodAccessesField(method, field, context.SemanticModel))
                    accessingMethods.Add(method);
            }

            if (accessingMethods.Count >= 2)
            {
                var fieldSyntax = FindFieldDeclaration(typeDecl, field.Name);

                if (fieldSyntax is not null)
                {
                    var variable = fieldSyntax.Declaration.Variables
                        .FirstOrDefault(v => v.Identifier.Text == field.Name);

                    if (variable is not null)
                        context.ReportDiagnostic(Diagnostic.Create(Rule, variable.Identifier.GetLocation(), type.Name, field.Name));
                }
            }
        }
    }

    private static FieldDeclarationSyntax? FindFieldDeclaration(TypeDeclarationSyntax typeDecl, string fieldName)
    {
        foreach (var member in typeDecl.Members)
        {
            if (member is FieldDeclarationSyntax fieldDecl)
            {
                foreach (var variable in fieldDecl.Declaration.Variables)
                {
                    if (variable.Identifier.Text == fieldName)
                        return fieldDecl;
                }
            }
        }

        return null;
    }

    private static bool HasPerClassLifetime(Compilation compilation, INamedTypeSymbol type)
    {
        var instanceLifetimeType = compilation.GetTypeByMetadataName(InstanceLifetimeTypeMetadataName);

        foreach (var attr in type.GetAttributes())
        {
            if (!IsInstanceLifetimeAttribute(attr))
                continue;

            if (attr.ConstructorArguments.Length != 1)
                continue;

            var arg = attr.ConstructorArguments[0];

            if (instanceLifetimeType is null || arg.Type is null)
                continue;

            if (!SymbolEqualityComparer.Default.Equals(arg.Type, instanceLifetimeType))
                continue;

            if (TryMatchEnumValueByName(instanceLifetimeType, arg.Value, PerClassMemberName))
                return true;
        }

        return false;
    }

    private static bool IsInstanceLifetimeAttribute(AttributeData attr)
    {
        var original = attr.AttributeClass?.OriginalDefinition;

        if (original is null)
            return false;

        if (original.MetadataName != "InstanceLifetimeAttribute")
            return false;

        if (original.ContainingType is not null)
            return false;

        var ns = original.ContainingNamespace;

        return ns is { IsGlobalNamespace: false }
               && ns.ToDisplayString() == "NBenchmark.Attributes";
    }

    private static bool TryMatchEnumValueByName(INamedTypeSymbol enumType, object? value, string memberName)
    {
        if (value is null)
            return false;

        if (value is INamedTypeSymbol namedMember)
            return namedMember.Name == memberName;

        var ordinal = Convert.ToInt32(value, CultureInfo.InvariantCulture);

        foreach (var member in enumType.GetMembers())
        {
            if (member is IFieldSymbol field
                && field.Name == memberName
                && field.HasConstantValue
                && field.ConstantValue is int memberValue
                && memberValue == ordinal)
                return true;
        }

        return false;
    }

    private static List<IFieldSymbol> GetMutableInstanceFields(INamedTypeSymbol type)
    {
        var fields = new List<IFieldSymbol>();

        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol field
                && !field.IsStatic
                && !field.IsReadOnly
                && !field.IsConst)
                fields.Add(field);
        }

        return fields;
    }

    private static bool MethodAccessesField(IMethodSymbol method, IFieldSymbol field, SemanticModel semanticModel)
    {
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();

            if (!syntax.SyntaxTree.Equals(semanticModel.SyntaxTree))
                continue;

            foreach (var identifier in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (identifier.Identifier.Text != field.Name)
                    continue;

                var symbolInfo = semanticModel.GetSymbolInfo(identifier);
                var referenced = symbolInfo.Symbol;

                if (referenced is not null && SymbolEqualityComparer.Default.Equals(referenced, field))
                    return true;
            }
        }

        return false;
    }
}
