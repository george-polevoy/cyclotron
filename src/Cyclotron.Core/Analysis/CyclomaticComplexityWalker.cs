using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cyclotron.Core.Analysis;

internal static class CyclomaticComplexityWalker
{
    public static int Calculate(SyntaxNode? bodyRoot)
    {
        if (bodyRoot is null)
        {
            return 1;
        }

        var walker = new Counter();
        walker.Visit(bodyRoot);
        return 1 + walker.Decisions;
    }

    private sealed class Counter : CSharpSyntaxWalker
    {
        public int Decisions { get; private set; }

        public override void VisitIfStatement(IfStatementSyntax node)
        {
            Decisions++;
            base.VisitIfStatement(node);
        }

        public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
        {
            Decisions++;
            base.VisitConditionalExpression(node);
        }

        public override void VisitForStatement(ForStatementSyntax node)
        {
            Decisions++;
            base.VisitForStatement(node);
        }

        public override void VisitForEachStatement(ForEachStatementSyntax node)
        {
            Decisions++;
            base.VisitForEachStatement(node);
        }

        public override void VisitWhileStatement(WhileStatementSyntax node)
        {
            Decisions++;
            base.VisitWhileStatement(node);
        }

        public override void VisitDoStatement(DoStatementSyntax node)
        {
            Decisions++;
            base.VisitDoStatement(node);
        }

        public override void VisitCatchClause(CatchClauseSyntax node)
        {
            Decisions++;
            base.VisitCatchClause(node);
        }

        public override void VisitCaseSwitchLabel(CaseSwitchLabelSyntax node)
        {
            Decisions++;
            base.VisitCaseSwitchLabel(node);
        }

        public override void VisitSwitchExpressionArm(SwitchExpressionArmSyntax node)
        {
            Decisions++;
            base.VisitSwitchExpressionArm(node);
        }

        public override void VisitBinaryExpression(BinaryExpressionSyntax node)
        {
            if (node.IsKind(SyntaxKind.LogicalAndExpression) || node.IsKind(SyntaxKind.LogicalOrExpression))
            {
                Decisions++;
            }

            base.VisitBinaryExpression(node);
        }

        public override void VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            // Local function bodies should not contribute to the enclosing member.
        }
    }
}
