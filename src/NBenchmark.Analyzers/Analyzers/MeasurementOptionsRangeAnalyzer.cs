using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class MeasurementOptionsRangeAnalyzer : DiagnosticAnalyzer
{
    private const int MaxSamplesLimit = 100_000;
    private const int MaxWarmupSamplesLimit = 10_000;
    private const int MaxOpsPerSampleLimit = 1 << 24;

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.MeasurementOptionsRange,
        "MeasurementOptions property value out of range",
        "{0}",
        "NBenchmark.Configuration",
        DiagnosticSeverity.Error,
        true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeImplicitObjectCreation, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeWithExpression, SyntaxKind.WithExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ObjectCreationExpressionSyntax creation)
            return;

        var typeSymbol = context.SemanticModel.GetSymbolInfo(creation.Type).Symbol as INamedTypeSymbol;

        if (typeSymbol is null)
            return;

        if (!BenchmarkSymbols.IsMeasurementOptionsType(typeSymbol))
            return;

        if (creation.Initializer is not InitializerExpressionSyntax initializer)
            return;

        foreach (var assignment in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
        {
            CheckAssignment(context, assignment);
        }
    }

    private static void AnalyzeImplicitObjectCreation(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not ImplicitObjectCreationExpressionSyntax creation)
            return;

        var typeSymbol = context.SemanticModel.GetTypeInfo(creation).Type as INamedTypeSymbol;

        if (typeSymbol is null)
            return;

        if (!BenchmarkSymbols.IsMeasurementOptionsType(typeSymbol))
            return;

        if (creation.Initializer is not InitializerExpressionSyntax initializer)
            return;

        foreach (var assignment in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
        {
            CheckAssignment(context, assignment);
        }
    }

    private static void AnalyzeWithExpression(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not WithExpressionSyntax with)
            return;

        var typeSymbol = context.SemanticModel.GetTypeInfo(with.Expression).Type as INamedTypeSymbol;

        if (typeSymbol is null)
            return;

        if (!BenchmarkSymbols.IsMeasurementOptionsType(typeSymbol))
            return;

        if (with.Initializer is not InitializerExpressionSyntax initializer)
            return;

        foreach (var assignment in initializer.Expressions.OfType<AssignmentExpressionSyntax>())
        {
            CheckAssignment(context, assignment);
        }
    }

    private static void CheckAssignment(SyntaxNodeAnalysisContext context, AssignmentExpressionSyntax assignment)
    {
        if (context.SemanticModel.GetSymbolInfo(assignment.Left).Symbol is not IPropertySymbol property)
            return;

        var constant = context.SemanticModel.GetConstantValue(assignment.Right);

        if (!constant.HasValue || constant.Value is null)
            return;

        switch (property.Name)
        {
            case "Samples":
                if (TryConvertToInt(constant.Value, out var iters) && (iters < 0 || iters > MaxSamplesLimit))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule,
                        assignment.GetLocation(),
                        $"Samples = {iters} is out of range. Must be 0-{MaxSamplesLimit}."));
                }

                break;

            case "WarmupSamples":
                if (TryConvertToInt(constant.Value, out var warmup) && (warmup < 0 || warmup > MaxWarmupSamplesLimit))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule,
                        assignment.GetLocation(),
                        $"WarmupSamples = {warmup} is out of range. Must be 0-{MaxWarmupSamplesLimit}."));
                }

                break;

            case "OpsPerSample":
                if (TryConvertToInt(constant.Value, out var ops) && (ops < 1 || ops > MaxOpsPerSampleLimit))
                {
                    context.ReportDiagnostic(Diagnostic.Create(Rule,
                        assignment.GetLocation(),
                        $"OpsPerSample = {ops} is out of range. Must be 1-{MaxOpsPerSampleLimit}."));
                }

                break;

            case "ConfidenceLevel":
                if (TryConvertToDouble(constant.Value, out var conf) && (conf <= 0 || conf >= 1))
                {
                    var display = conf.ToString(CultureInfo.InvariantCulture);

                    context.ReportDiagnostic(Diagnostic.Create(Rule,
                        assignment.GetLocation(),
                        $"ConfidenceLevel = {display} is out of range. Must be strictly between 0 and 1."));
                }

                break;
        }
    }

    private static bool TryConvertToInt(object value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case uint ui when ui <= int.MaxValue:
                result = (int)ui;
                return true;
            case long l when l is >= int.MinValue and <= int.MaxValue:
                result = (int)l;
                return true;
        }

        result = 0;
        return false;
    }

    private static bool TryConvertToDouble(object value, out double result)
    {
        switch (value)
        {
            case double d:
                result = d;
                return true;
            case float f:
                result = f;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case uint ui:
                result = ui;
                return true;
            case ulong ul:
                result = ul;
                return true;
            case short s:
                result = s;
                return true;
            case ushort us:
                result = us;
                return true;
            case byte b:
                result = b;
                return true;
            case sbyte sb:
                result = sb;
                return true;
        }

        result = 0;
        return false;
    }
}
