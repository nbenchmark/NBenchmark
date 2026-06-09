using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NBenchmark.Analyzers.Shared;

internal sealed class SideEffectWalker : CSharpSyntaxWalker
{
    private readonly SemanticModel _semanticModel;

    public bool HasAnyEffect { get; private set; }

    public SideEffectWalker(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        HasAnyEffect = true;
    }

    public override void VisitAssignmentExpression(AssignmentExpressionSyntax node)
    {
        if (node.Left is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
        {
            HasAnyEffect = true;
        }
        else if (node.Left is IdentifierNameSyntax identifier)
        {
            if (IsFieldOrProperty(identifier))
                HasAnyEffect = true;
        }
    }

    public override void VisitPostfixUnaryExpression(PostfixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.PostIncrementExpression) || node.IsKind(SyntaxKind.PostDecrementExpression))
            CheckUnaryOperand(node.Operand);
    }

    public override void VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
    {
        if (node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PreDecrementExpression))
            CheckUnaryOperand(node.Operand);
    }

    private void CheckUnaryOperand(ExpressionSyntax operand)
    {
        if (operand is MemberAccessExpressionSyntax)
        {
            HasAnyEffect = true;
        }
        else if (operand is IdentifierNameSyntax identifier)
        {
            if (IsFieldOrProperty(identifier))
                HasAnyEffect = true;
        }
    }

    private bool IsFieldOrProperty(IdentifierNameSyntax identifier)
    {
        var symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
        return symbol is IFieldSymbol or IPropertySymbol;
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        HasAnyEffect = true;
    }

    public override void VisitArrayCreationExpression(ArrayCreationExpressionSyntax node)
    {
        HasAnyEffect = true;
    }

    public override void VisitImplicitArrayCreationExpression(ImplicitArrayCreationExpressionSyntax node)
    {
        HasAnyEffect = true;
    }

    public override void VisitArgument(ArgumentSyntax node)
    {
        var kind = node.RefOrOutKeyword.Kind();
        if (kind == SyntaxKind.RefKeyword || kind == SyntaxKind.OutKeyword)
        {
            HasAnyEffect = true;
        }
    }

    public override void VisitAwaitExpression(AwaitExpressionSyntax node)
    {
        HasAnyEffect = true;
    }
}
