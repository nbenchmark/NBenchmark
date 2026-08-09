using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

/// <summary>
///     Reports a member carrying both <c>[InProcess]</c> and <c>[IsolatedProcess]</c>.
/// </summary>
/// <remarks>
///     <para>
///         The two attributes ask for opposite things, and until now the conflict resolved silently in
///         favour of <c>[InProcess]</c> - whichever the author meant, they got the host process and no
///         indication that the other attribute had been read and discarded. That is the worst available
///         outcome for a pair of attributes whose whole subject is where a measurement runs: one of them
///         is a request for a clean-room reading, and losing it quietly means numbers carrying the
///         host's JIT and GC configuration under a declaration that asked for neither.
///     </para>
///     <para>
///         An error rather than a warning because there is no reading of the source under which both are
///         wanted. Unlike a method-level attribute overriding a class-level one - a deliberate and
///         useful shape, and not reported here - two on one member cannot be an intent, only a mistake
///         or a leftover.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConflictingIsolationAttributesAnalyzer : DiagnosticAnalyzer
{
    private const string InProcessFullName = "NBenchmark.Attributes.InProcessAttribute";
    private const string IsolatedProcessFullName = "NBenchmark.Attributes.IsolatedProcessAttribute";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.ConflictingIsolationAttributes,
        "Conflicting isolation attributes",
        "'{0}' carries both [InProcess] and [IsolatedProcess], which ask for opposite things. Remove "
        + "one: [InProcess] measures in the host process, [IsolatedProcess] measures in a dedicated "
        + "worker.",
        "NBenchmark.Usage",
        DiagnosticSeverity.Error,
        true,
        "The two attributes cannot both be honoured. Resolving the conflict silently would discard a "
        + "request about where the measurement runs, which is the one thing neither attribute can be "
        + "wrong about.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

        context.RegisterSyntaxNodeAction(
            AnalyzeMember,
            SyntaxKind.MethodDeclaration,
            SyntaxKind.ClassDeclaration,
            SyntaxKind.RecordDeclaration,
            SyntaxKind.StructDeclaration,
            SyntaxKind.RecordStructDeclaration);
    }

    private static void AnalyzeMember(SyntaxNodeAnalysisContext context)
    {
        var symbol = context.SemanticModel.GetDeclaredSymbol(context.Node);

        if (symbol is null)
            return;

        var hasInProcess = false;
        var hasIsolated = false;
        Location? location = null;

        foreach (var attribute in symbol.GetAttributes())
        {
            var name = attribute.AttributeClass?.OriginalDefinition?.ToDisplayString()
                       ?? attribute.AttributeClass?.ToDisplayString();

            switch (name)
            {
                case InProcessFullName:
                    hasInProcess = true;
                    break;
                case IsolatedProcessFullName:
                    hasIsolated = true;

                    // Reported on [IsolatedProcess] rather than on the declaration name, because that is
                    // the attribute the old resolution threw away - so the squiggle sits on the request
                    // that was being lost.
                    location = attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken)
                        .GetLocation();

                    break;
            }
        }

        if (!hasInProcess || !hasIsolated)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            location ?? context.Node.GetLocation(),
            symbol.Name));
    }
}
