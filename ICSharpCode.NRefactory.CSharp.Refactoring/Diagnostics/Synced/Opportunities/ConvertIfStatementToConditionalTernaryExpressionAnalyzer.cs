//
// ConvertIfStatementToConditionalTernaryExpressionAnalyzer.cs
//
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
//
// Copyright (c) 2013 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CodeFixes;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Text;
using System.Threading;
using ICSharpCode.NRefactory6.CSharp.Refactoring;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.FindSymbols;
using ICSharpCode.NRefactory6.CSharp.CodeRefactorings;

namespace ICSharpCode.NRefactory6.CSharp.Diagnostics
{
	[DiagnosticAnalyzer(LanguageNames.CSharp)]
	[NRefactoryCodeDiagnosticAnalyzer(AnalysisDisableKeyword = "ConvertIfStatementToConditionalTernaryExpression")]
	public class ConvertIfStatementToConditionalTernaryExpressionAnalyzer : GatherVisitorDiagnosticAnalyzer
	{
		internal const string DiagnosticId  = "ConvertIfStatementToConditionalTernaryExpressionAnalyzer";
		const string Description            = "Convert 'if' to '?:'";
		const string MessageFormat          = "Convert to '?:' expression";
		const string Category               = DiagnosticAnalyzerCategories.Opportunities;

		static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor (DiagnosticId, Description, MessageFormat, Category, DiagnosticSeverity.Info, true, "'if' statement can be re-written as '?:' expression");

		public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics {
			get {
				return ImmutableArray.Create(Rule);
			}
		}

		protected override CSharpSyntaxWalker CreateVisitor (SemanticModel semanticModel, Action<Diagnostic> addDiagnostic, CancellationToken cancellationToken)
		{
			return new GatherVisitor(semanticModel, addDiagnostic, cancellationToken);
		}

		public static bool IsComplexExpression(ExpressionSyntax expr)
		{
			var loc = expr.GetLocation().GetLineSpan();
			return loc.StartLinePosition.Line != loc.EndLinePosition.Line ||
				expr is ConditionalExpressionSyntax ||
				expr is BinaryExpressionSyntax;
		}

		public static bool IsComplexCondition(ExpressionSyntax expr)
		{
			var loc = expr.GetLocation().GetLineSpan();
			if (loc.StartLinePosition.Line != loc.EndLinePosition.Line)
				return true;

			if (expr is LiteralExpressionSyntax || expr is IdentifierNameSyntax || expr is MemberAccessExpressionSyntax || expr is InvocationExpressionSyntax)
				return false;

			var pexpr = expr as ParenthesizedExpressionSyntax;
			if (pexpr != null)
				return IsComplexCondition(pexpr.Expression);

			var uOp = expr as PrefixUnaryExpressionSyntax;
			if (uOp != null)
				return IsComplexCondition(uOp.Operand);

			var bop = expr as BinaryExpressionSyntax;
			if (bop == null)
				return true;
			return !(bop.IsKind(SyntaxKind.GreaterThanExpression) ||
				bop.IsKind(SyntaxKind.GreaterThanOrEqualExpression) ||
				bop.IsKind(SyntaxKind.EqualsExpression) ||
				bop.IsKind(SyntaxKind.NotEqualsExpression) ||
				bop.IsKind(SyntaxKind.LessThanExpression) ||
				bop.IsKind(SyntaxKind.LessThanOrEqualExpression));
		}

		class GatherVisitor : GatherVisitorBase<ConvertIfStatementToConditionalTernaryExpressionAnalyzer>
		{
			public GatherVisitor(SemanticModel semanticModel, Action<Diagnostic> addDiagnostic, CancellationToken cancellationToken)
				: base (semanticModel, addDiagnostic, cancellationToken)
			{
			}

			public override void VisitIfStatement(IfStatementSyntax node)
			{
				base.VisitIfStatement(node);

				ExpressionSyntax condition, target;
				AssignmentExpressionSyntax trueAssignment, falseAssignment;
				if (!ConvertIfStatementToConditionalTernaryExpressionCodeRefactoringProvider.ParseIfStatement(node, out condition, out target, out trueAssignment, out falseAssignment))
					return;
				if (IsComplexCondition(condition) || IsComplexExpression(trueAssignment.Right) || IsComplexExpression(falseAssignment.Right))
					return;

				AddDiagnosticAnalyzer(Diagnostic.Create(Rule, node.IfKeyword.GetLocation()));
			}
		}
	}

	[ExportCodeFixProvider(LanguageNames.CSharp), System.Composition.Shared]
	public class ConvertIfStatementToConditionalTernaryExpressionFixProvider : NRefactoryCodeFixProvider
	{
		protected override IEnumerable<string> InternalGetFixableDiagnosticIds()
		{
			yield return ConvertIfStatementToConditionalTernaryExpressionAnalyzer.DiagnosticId;
		}

		public override FixAllProvider GetFixAllProvider()
		{
			return WellKnownFixAllProviders.BatchFixer;
		}

		public async override Task RegisterCodeFixesAsync(CodeFixContext context)
		{
			var document = context.Document;
			var cancellationToken = context.CancellationToken;
			var span = context.Span;
			var diagnostics = context.Diagnostics;
			var root = await document.GetSyntaxRootAsync(cancellationToken);
			var diagnostic = diagnostics.First ();
			var node = root.FindNode(context.Span) as IfStatementSyntax;

			ExpressionSyntax condition, target;
			AssignmentExpressionSyntax trueAssignment, falseAssignment;
			if (!ConvertIfStatementToConditionalTernaryExpressionCodeRefactoringProvider.ParseIfStatement(node, out condition, out target, out trueAssignment, out falseAssignment))
				return;
			var newRoot = root.ReplaceNode((SyntaxNode)node,
				SyntaxFactory.ExpressionStatement(
					SyntaxFactory.AssignmentExpression(
						trueAssignment.Kind(),
						trueAssignment.Left,
						SyntaxFactory.ConditionalExpression(condition, trueAssignment.Right, falseAssignment.Right)
					)
				).WithAdditionalAnnotations(Formatter.Annotation)
			);
			context.RegisterCodeFix(CodeActionFactory.Create(node.Span, diagnostic.Severity, "Convert to '?:' expression", document.WithSyntaxRoot(newRoot)), diagnostic);
		}
	}
}