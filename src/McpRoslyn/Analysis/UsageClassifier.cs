using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace McpRoslyn.Analysis;

/// <summary>
/// Classifies how a reference location uses a value-like symbol (field/property/local/parameter/event):
/// read, write, read+write, or name-only (nameof). C# only; other languages return "".
/// </summary>
public static class UsageClassifier
{
    public const string Read = "r";
    public const string Write = "W";
    public const string ReadWrite = "rw";
    public const string NameOnly = "n";

    public static bool IsClassifiable(ISymbol symbol) =>
        symbol.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Local or SymbolKind.Parameter or SymbolKind.Event;

    public static string Classify(SyntaxNode root, TextSpan span)
    {
        if (root.Language != LanguageNames.CSharp)
            return "";

        var node = root.FindNode(span, getInnermostNodeForTie: true);

        // nameof(X) — the reference exists only as a name.
        for (SyntaxNode? cur = node; cur is not null; cur = cur.Parent)
        {
            if (cur is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } })
                return NameOnly;
            if (cur is StatementSyntax or MemberDeclarationSyntax)
                break;
        }

        // Climb through the access chain while we're the "name" side of it, so `a.B.Field` is
        // classified by what happens to the whole `a.B.Field` expression.
        var expr = node as ExpressionSyntax ?? node.Parent as ExpressionSyntax;
        if (expr is null)
            return Read;

        while (expr.Parent is ExpressionSyntax parentExpr && IsAccessNameSide(expr, parentExpr))
            expr = parentExpr;

        switch (expr.Parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Left == expr:
                return assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) ? Write : ReadWrite;
            case PrefixUnaryExpressionSyntax prefix
                when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression):
                return ReadWrite;
            case PostfixUnaryExpressionSyntax postfix
                when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression):
                return ReadWrite;
            case ArgumentSyntax argument when argument.Expression == expr:
                if (argument.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword))
                    return Write;
                if (argument.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword))
                    return ReadWrite;
                return Read;
            default:
                return Read;
        }
    }

    private static bool IsAccessNameSide(ExpressionSyntax expr, ExpressionSyntax parent) => parent switch
    {
        MemberAccessExpressionSyntax member => member.Name == expr,
        MemberBindingExpressionSyntax binding => binding.Name == expr,
        ConditionalAccessExpressionSyntax conditional => conditional.WhenNotNull == expr,
        ParenthesizedExpressionSyntax => true,
        ElementAccessExpressionSyntax element => element.Expression == expr,
        _ => false,
    };
}
