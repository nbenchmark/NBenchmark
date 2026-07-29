using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using NBenchmark.Analyzers.Shared;

namespace NBenchmark.Analyzers.Analyzers;

/// <summary>
///     Reports, at compile time, that a benchmark lambda captures state from its enclosing scope and
///     will therefore be measured in the host process rather than in an isolated worker.
/// </summary>
/// <remarks>
///     <para>
///         NBenchmark addresses a benchmark body across a process boundary by resolving the method
///         the compiler already emitted - it never serializes or regenerates the body. A lambda that
///         captures cannot be addressed that way, because its captured values live in this process
///         and there is no honest way to reproduce them in another. Reconstructing them was tried and
///         rejected: a fabricated closure did not throw, it returned plausible wrong numbers.
///     </para>
///     <para>
///         So a capturing body is refused for isolation and measured in the host instead. That is
///         reported at runtime and its ratio is withheld, but by then the run has happened. This
///         diagnostic moves the news to the point where the developer can still act on it, and names
///         the symbols responsible - which the runtime cannot do as precisely, because by then they
///         are fields on a compiler-generated class.
///     </para>
///     <para>
///         The rule is per-lambda, and measurement confirms the runtime's decision is too: a
///         non-capturing lambda is hoisted to the shared field-less singleton even when a sibling in
///         the same scope captures, so it keeps its isolation and this rule stays silent. Scope
///         merging does affect two lambdas that <i>both</i> capture - they share one display class, so
///         each runtime refusal names the other's symbols - but this diagnostic names only what its
///         own lambda captures, which is the more useful answer. See <c>BodyRefCaptureTests</c>, which
///         pins both behaviours against the runtime.
///     </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CapturingBodyAnalyzer : DiagnosticAnalyzer
{
    private const string BenchmarkClassName = "NBenchmark.Benchmark";

    private static readonly ImmutableHashSet<string> TargetMethodNames =
        ImmutableHashSet.Create("Run", "RunAsync", "RunRaw", "RunRawAsync");

    /// <summary>
    ///     Information rather than a warning. Capturing is the idiomatic way to write a benchmark
    ///     over prepared data - <c>var data = Build(); Benchmark.Run(() =&gt; Sort(data));</c> - and
    ///     warning on it would push people towards contorted code to silence a build. What it costs
    ///     is measurement fidelity, which is worth knowing about and is not a defect.
    /// </summary>
    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticIds.CapturingBody,
        "Benchmark body captures state and cannot be isolated",
        "The lambda passed to Benchmark.{0} captures {1} from its enclosing scope, so it will be "
        + "measured in this process instead of an isolated worker. In-process measurement inherits "
        + "the host's JIT and GC state. To isolate it, move the captured state inside the lambda or "
        + "use a [Benchmark] class.",
        "NBenchmark.Performance",
        DiagnosticSeverity.Info,
        true,
        description:
        "A capturing lambda cannot be addressed across a process boundary, because its captured "
        + "values exist only in the process that created them. NBenchmark refuses to reconstruct "
        + "them - a fabricated closure returns plausible wrong measurements rather than failing - so "
        + "the body is measured in the host process and labelled as such in the report.");

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

        if (invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax lambda)
            return;

        if (context.SemanticModel.GetSymbolInfo(invocation).Symbol is not IMethodSymbol methodSymbol)
            return;

        if (methodSymbol.ContainingType?.ToDisplayString() != BenchmarkClassName)
            return;

        if (!TargetMethodNames.Contains(methodSymbol.Name))
            return;

        var captured = DescribeCaptures(context.SemanticModel, lambda);

        if (captured is null)
            return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, lambda.GetLocation(), methodSymbol.Name, captured));
    }

    /// <summary>
    ///     Names what <paramref name="lambda" /> captures, or <c>null</c> when it captures nothing.
    /// </summary>
    /// <remarks>
    ///     Data-flow analysis is the only reliable way to answer this. A syntactic walk looking for
    ///     identifiers declared elsewhere would have to reimplement scoping, shadowing and definite
    ///     assignment, and would get <c>this</c> capture through an implicit field reference wrong -
    ///     which is the case a developer is least likely to spot unaided.
    /// </remarks>
    private static string? DescribeCaptures(SemanticModel model, LambdaExpressionSyntax lambda)
    {
        var flow = model.AnalyzeDataFlow(lambda);

        if (flow is null || !flow.Succeeded)
            return null;

        var names = flow.Captured
            .Where(symbol => !IsOwnParameter(symbol, lambda))
            .Select(DescribeSymbol)
            .Where(name => name is not null)
            .Distinct()
            .OrderBy(name => name, System.StringComparer.Ordinal)
            .ToList();

        // An instance member touched without an explicit receiver captures `this`, and `this` is not
        // a local, so data flow does not list it among the captured variables. It is a capture in
        // every way that matters here: the closure holds a reference to an object this process owns.
        if (CapturesThis(model, lambda))
            names.Insert(0, "this");

        return names.Count switch
        {
            0 => null,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[names.Count - 1]}",
        };
    }

    /// <summary>
    ///     A lambda's own parameters are not captures - they are supplied at each invocation. They
    ///     can appear in the captured set when a <i>nested</i> lambda closes over them, which says
    ///     nothing about whether the outer body can be addressed.
    /// </summary>
    private static bool IsOwnParameter(ISymbol symbol, LambdaExpressionSyntax lambda)
        => symbol is IParameterSymbol parameter
           && parameter.ContainingSymbol?.DeclaringSyntaxReferences
               .Any(reference => reference.GetSyntax() == lambda) == true;

    private static string? DescribeSymbol(ISymbol symbol)
        => symbol switch
        {
            IParameterSymbol { IsThis: true } => "this",
            ILocalSymbol or IParameterSymbol or IRangeVariableSymbol => $"'{symbol.Name}'",
            _ => null,
        };

    /// <summary>
    ///     Whether the body reads the enclosing instance, either through an explicit <c>this</c> or
    ///     by naming an instance member without a receiver.
    /// </summary>
    private static bool CapturesThis(SemanticModel model, LambdaExpressionSyntax lambda)
    {
        var body = lambda.Body;

        if (body is null)
            return false;

        foreach (var node in body.DescendantNodesAndSelf())
        {
            if (node is ThisExpressionSyntax or BaseExpressionSyntax)
                return true;

            if (node is not IdentifierNameSyntax identifier)
                continue;

            // Only a bare identifier implies `this`. `other.Field` names an instance member too, but
            // through a receiver that is itself either a capture (already reported) or a local.
            if (identifier.Parent is MemberAccessExpressionSyntax access && access.Name == identifier)
                continue;

            if (model.GetSymbolInfo(identifier).Symbol is { IsStatic: false } member
                && member.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Method or SymbolKind.Event)
            {
                return true;
            }
        }

        return false;
    }
}
