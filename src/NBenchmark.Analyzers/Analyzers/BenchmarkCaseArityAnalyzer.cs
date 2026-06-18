using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BenchmarkCaseArityAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.BenchmarkCaseArity,
        "[BenchmarkCase] / [BenchmarkCases] must match method parameters",
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

        var method = context.SemanticModel.GetDeclaredSymbol(methodDecl);

        if (method is null)
            return;

        if (!BenchmarkSymbols.HasBenchmarkAttribute(method))
            return;

        var parameterCount = method.Parameters.Length;
        var caseAttributes = GetAttributes(method, BenchmarkSymbols.IsBenchmarkCaseAttribute);
        var casesAttribute = GetAttribute(method, BenchmarkSymbols.IsBenchmarkCasesAttribute);

        if (casesAttribute is not null)
        {
            AnalyzeCasesSource(context, methodDecl, method, casesAttribute, parameterCount);
            return;
        }

        if (parameterCount == 0 && caseAttributes.Length > 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has [BenchmarkCase] but takes no parameters."));

            return;
        }

        if (parameterCount > 0 && caseAttributes.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has {parameterCount} parameter(s) but no [BenchmarkCase] or [BenchmarkCases]. Add one [BenchmarkCase(...)] per argument set."));

            return;
        }

        foreach (var attr in caseAttributes)
        {
            var effectiveCount = GetEffectiveArgumentCount(attr);

            if (effectiveCount == 0 && parameterCount > 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule,
                    methodDecl.Identifier.GetLocation(),
                    $"Method '{method.Name}' expects {parameterCount} argument(s) but a [BenchmarkCase] attribute supplies none."));
            }
            else if (effectiveCount > 0 && effectiveCount != parameterCount)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule,
                    methodDecl.Identifier.GetLocation(),
                    $"Method '{method.Name}' expects {parameterCount} argument(s) but a [BenchmarkCase] attribute supplies {effectiveCount}."));
            }
        }
    }

    private static void AnalyzeCasesSource(
        SyntaxNodeAnalysisContext context,
        MethodDeclarationSyntax methodDecl,
        IMethodSymbol method,
        AttributeData casesAttribute,
        int parameterCount)
    {
        if (parameterCount == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has [BenchmarkCases] but takes no parameters."));

            return;
        }

        var sourceName = GetSourceName(casesAttribute);

        if (sourceName is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has [BenchmarkCases] with no source member name. "
                + "Specify the name of a parameterless method that returns IEnumerable<ValueTuple<...>>."));

            return;
        }

        var declaringType = method.ContainingType;

        if (declaringType is null)
            return;

        var sourceMembers = declaringType.GetMembers(sourceName);

        if (sourceMembers.Length == 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has [BenchmarkCases(\"{sourceName}\")] but no member named '{sourceName}' was found on type '{declaringType.Name}'."));

            return;
        }

        var sourceMethod = sourceMembers.OfType<IMethodSymbol>().FirstOrDefault();

        if (sourceMethod is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' references [BenchmarkCases(\"{sourceName}\")] but '{sourceName}' is not a method."));

            return;
        }

        if (sourceMethod.IsGenericMethod)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' references source method '{sourceName}' via [BenchmarkCases], but the source method must not be generic."));

            return;
        }

        if (sourceMethod.Parameters.Length > 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' references source method '{sourceName}' via [BenchmarkCases], but the source method must have no parameters."));

            return;
        }

        var returnType = sourceMethod.ReturnType;

        if (returnType is not INamedTypeSymbol namedReturn)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Source method '{sourceName}' must return IEnumerable<(ValueTuple<...>)>."));

            return;
        }

        var enumerableInterface = FindIEnumerable(namedReturn);

        if (enumerableInterface is null)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' references source method '{sourceName}' which returns '{returnType.Name}', but the source must return IEnumerable<(ValueTuple<...>)>."));

            return;
        }

        var elementType = enumerableInterface.TypeArguments[0];

        if (elementType is not INamedTypeSymbol elementNamed || !elementNamed.IsValueType || !IsValueTupleType(elementNamed))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' references source method '{sourceName}' which returns IEnumerable<{elementType.Name}>, but the element type must be a ValueTuple."));

            return;
        }

        var tupleArity = GetValueTupleArity(elementNamed);

        if (tupleArity != parameterCount)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has [BenchmarkCases(\"{sourceName}\")] where the source yields "
                + $"ValueTuple with {tupleArity} element(s), but the method has {parameterCount} parameter(s)."));
        }
        else if (tupleArity > 7)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule,
                methodDecl.Identifier.GetLocation(),
                $"Method '{method.Name}' has [BenchmarkCases(\"{sourceName}\")] where the source yields "
                + $"a ValueTuple with {tupleArity} element(s). NBenchmark supports at most 7 parameters for [BenchmarkCases] sources."));
        }
    }

    private static INamedTypeSymbol? FindIEnumerable(INamedTypeSymbol type)
    {
        if (type.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
            return type;

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                return iface;
        }

        return null;
    }

    private static bool IsValueTupleType(INamedTypeSymbol type)
    {
        if (!type.IsGenericType)
            return false;

        var name = type.MetadataName;
        return name.StartsWith("ValueTuple`", StringComparison.Ordinal);
    }

    private static int GetValueTupleArity(INamedTypeSymbol tupleType)
    {
        var typeArgs = tupleType.TypeArguments;

        if (typeArgs.Length == 8 && IsValueTupleType(tupleType))
        {
            var rest = typeArgs[7];
            if (rest is INamedTypeSymbol namedRest && IsValueTupleType(namedRest))
                return 7 + GetValueTupleArity(namedRest);
        }

        return typeArgs.Length;
    }

    private static string? GetSourceName(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 0)
            return null;

        var arg = attr.ConstructorArguments[0];

        if (arg.Kind == TypedConstantKind.Primitive && arg.Value is string name)
            return name;

        return null;
    }

    private static int GetEffectiveArgumentCount(AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 0)
            return 0;

        if (attr.ConstructorArguments.Length == 1 &&
            attr.ConstructorArguments[0].Kind == TypedConstantKind.Array)
            return attr.ConstructorArguments[0].Values.Length;

        return attr.ConstructorArguments.Length;
    }

    private static ImmutableArray<AttributeData> GetAttributes(IMethodSymbol method, Func<INamedTypeSymbol?, bool> predicate)
    {
        var builder = ImmutableArray.CreateBuilder<AttributeData>();

        foreach (var attr in method.GetAttributes())
        {
            if (predicate(attr.AttributeClass))
                builder.Add(attr);
        }

        return builder.ToImmutable();
    }

    private static AttributeData? GetAttribute(IMethodSymbol method, Func<INamedTypeSymbol?, bool> predicate)
    {
        foreach (var attr in method.GetAttributes())
        {
            if (predicate(attr.AttributeClass))
                return attr;
        }

        return null;
    }
}
