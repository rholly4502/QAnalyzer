using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi predicate non-SARGable pada klausa WHERE/JOIN ON/MERGE ON via AST ScriptDom
/// (mis. fungsi/konversi/aritmatika/ISNULL pada kolom atau LIKE dengan wildcard di awal).
/// </summary>
public sealed class NonSargablePredicateRule : IAstSqlRule
{
    public string Id => "NON_SARGABLE";
    public string Title => "Predicate non-SARGable";
    public RiskLevel Level => RiskLevel.Warning;

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var fragment = SqlAstParser.Parse(sql);
        return fragment is null ? [] : Evaluate(fragment);
    }

    IEnumerable<Diagnostic> IAstSqlRule.Evaluate(TSqlFragment fragment) => Evaluate(fragment);

    private IEnumerable<Diagnostic> Evaluate(TSqlFragment fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var visitor = new PredicateScopeVisitor(Level, Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics;
    }

    private sealed class PredicateScopeVisitor(RiskLevel defaultLevel, string ruleId, string title) : TSqlFragmentVisitor
    {
        public List<Diagnostic> Diagnostics { get; } = [];

        public override void Visit(WhereClause node) => InspectBooleanExpression(node.SearchCondition);

        public override void Visit(QualifiedJoin node) => InspectBooleanExpression(node.SearchCondition);

        public override void Visit(MergeSpecification node) => InspectBooleanExpression(node.SearchCondition);

        private void InspectBooleanExpression(BooleanExpression? expression)
        {
            switch (expression)
            {
                case null:
                    return;
                case BooleanParenthesisExpression paren:
                    InspectBooleanExpression(paren.Expression);
                    return;
                case BooleanBinaryExpression binary:
                    InspectBooleanExpression(binary.FirstExpression);
                    InspectBooleanExpression(binary.SecondExpression);
                    return;
                case BooleanNotExpression notExpr:
                    InspectBooleanExpression(notExpr.Expression);
                    return;
                case BooleanComparisonExpression comparison:
                    InspectComparison(comparison);
                    return;
                case LikePredicate like:
                    InspectLike(like);
                    return;
                case InPredicate inPred:
                    InspectOperand(inPred.Expression, allowArithmeticOnColumn: true);
                    return;
                case BooleanTernaryExpression ternary:
                    InspectOperand(ternary.FirstExpression, allowArithmeticOnColumn: true);
                    if (!ContainsDirectColumnReference(ternary.FirstExpression))
                    {
                        InspectOperand(ternary.SecondExpression, allowArithmeticOnColumn: true);
                        InspectOperand(ternary.ThirdExpression, allowArithmeticOnColumn: true);
                    }
                    return;
                case BooleanIsNullExpression isNull:
                    InspectOperand(isNull.Expression, allowArithmeticOnColumn: true);
                    return;
                case SubqueryComparisonPredicate subComp:
                    InspectOperand(subComp.Expression, allowArithmeticOnColumn: true);
                    return;
            }
        }

        private void InspectComparison(BooleanComparisonExpression comparison)
        {
            var lhs = Unwrap(comparison.FirstExpression);
            var rhs = Unwrap(comparison.SecondExpression);

            var lhsHasCol = ContainsDirectColumnReference(lhs);
            var rhsHasCol = ContainsDirectColumnReference(rhs);
            if (!lhsHasCol && !rhsHasCol)
            {
                return;
            }

            InspectOperand(lhs, allowArithmeticOnColumn: !rhsHasCol || rhs is not ColumnReferenceExpression);
            InspectOperand(rhs, allowArithmeticOnColumn: !lhsHasCol || lhs is not ColumnReferenceExpression);
        }

        private void InspectLike(LikePredicate like)
        {
            InspectOperand(like.FirstExpression, allowArithmeticOnColumn: true);

            if (ContainsDirectColumnReference(like.FirstExpression)
                && HasLeadingWildcard(like.SecondExpression, like.EscapeExpression))
            {
                AddDiagnostic(
                    "Pola LIKE diawali wildcard ('%' atau '_') pada kolom — mencegah Index Seek dan memaksa Index/Table Scan.",
                    "Hindari wildcard di awal pola; gunakan pencarian prefix (LIKE 'nilai%') atau Full-Text Index.",
                    like);
            }
        }

        private void InspectOperand(ScalarExpression? expression, bool allowArithmeticOnColumn)
        {
            var unwrapped = Unwrap(expression);
            if (unwrapped is null
                || unwrapped is ColumnReferenceExpression
                || unwrapped is ScalarSubquery
                || !ContainsDirectColumnReference(unwrapped))
            {
                return;
            }

            if (IsNullCoalescing(unwrapped))
            {
                AddDiagnostic(
                    "Fungsi ISNULL/COALESCE pada kolom di predicate mencegah SQL Server menggunakan Index Seek.",
                    "Ubah predicate menjadi eksplisit (mis. col IS NULL OR col = nilai) agar index tetap dapat digunakan.",
                    unwrapped);
                return;
            }

            if (unwrapped is FunctionCall or LeftFunctionCall or RightFunctionCall
                or CastCall or ConvertCall or TryCastCall or TryConvertCall
                or ParseCall or TryParseCall or NullIfExpression or IIfCall
                or AtTimeZoneCall or CaseExpression)
            {
                AddDiagnostic(
                    "Fungsi atau konversi pada kolom di predicate (WHERE/ON) mencegah Index Seek dan memicu scan.",
                    "Pindahkan fungsi/konversi ke sisi nilai pembanding (mis. ganti YEAR(col) = 2025 menjadi rentang col >= '2025-01-01' AND col < '2026-01-01') atau gunakan computed column terindeks.",
                    unwrapped);
                return;
            }

            if (allowArithmeticOnColumn
                && (unwrapped is BinaryExpression
                    || (unwrapped is UnaryExpression unary
                        && unary.UnaryExpressionType is UnaryExpressionType.Negative or UnaryExpressionType.BitwiseNot)))
            {
                AddDiagnostic(
                    "Operasi aritmatika atau ekspresi pada kolom di predicate mencegah SQL Server menggunakan Index Seek.",
                    "Pindahkan operasi aritmatika ke sisi nilai pembanding (mis. ganti col + 1 = 10 menjadi col = 9) agar kolom tetap SARGable.",
                    unwrapped);
            }
        }

        private void AddDiagnostic(string message, string suggestion, TSqlFragment node) =>
            Diagnostics.Add(new Diagnostic(
                defaultLevel,
                ruleId,
                title,
                message,
                suggestion,
                node.StartLine,
                node.StartColumn));
    }

    private static bool IsNullCoalescing(ScalarExpression expression) =>
        expression is CoalesceExpression
        || (expression is FunctionCall fn
            && fn.CallTarget is null
            && fn.FunctionName.Value.Equals("ISNULL", StringComparison.OrdinalIgnoreCase));

    private static ScalarExpression? Unwrap(ScalarExpression? expression)
    {
        while (expression is ParenthesisExpression paren)
        {
            expression = paren.Expression;
        }

        return expression;
    }

    private static bool HasLeadingWildcard(ScalarExpression? patternExpr, ScalarExpression? escapeExpr)
    {
        char? escapeChar = Unwrap(escapeExpr) is StringLiteral { Value.Length: 1 } escLit
            ? escLit.Value[0]
            : null;

        var text = TryGetLeadingStringLiteral(patternExpr);
        return text is { Length: > 0 }
            && (text[0] == '%' || text[0] == '_')
            && (escapeChar is null || text[0] != escapeChar.Value);
    }

    private static string? TryGetLeadingStringLiteral(ScalarExpression? expression) =>
        Unwrap(expression) switch
        {
            StringLiteral lit => lit.Value,
            BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add or BinaryExpressionType.Concat } bin
                => TryGetLeadingStringLiteral(bin.FirstExpression),
            FunctionCall fn when fn.CallTarget is null
                && fn.FunctionName.Value.Equals("CONCAT", StringComparison.OrdinalIgnoreCase)
                && fn.Parameters.Count > 0
                => TryGetLeadingStringLiteral(fn.Parameters[0]),
            _ => null,
        };

    private static bool ContainsDirectColumnReference(TSqlFragment? node)
    {
        if (node is null)
        {
            return false;
        }

        var finder = new DirectColumnFinder();
        node.Accept(finder);
        return finder.Found;
    }

    private sealed class DirectColumnFinder : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase)
        {
            "DATEADD",
            "DATEDIFF",
            "DATEDIFF_BIG",
            "DATEPART",
            "DATENAME",
            "DATE_BUCKET",
            "DATETRUNC",
        };

        public bool Found { get; private set; }

        public override void ExplicitVisit(ScalarSubquery node)
        {
            // Jangan ikutkan referensi kolom di dalam subquery skalar ke ekspresi luar.
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (Found)
            {
                return;
            }

            if (node.CallTarget is null && DatePartFunctions.Contains(node.FunctionName.Value))
            {
                // Parameter pertama fungsi tanggal (DAY, MONTH, YEAR, dll.) di-parse sebagai ColumnReferenceExpression.
                for (var i = 1; i < node.Parameters.Count && !Found; i++)
                {
                    node.Parameters[i].Accept(this);
                }

                return;
            }

            node.AcceptChildren(this);
        }

        public override void Visit(ColumnReferenceExpression node)
        {
            if (node.ColumnType != ColumnType.Wildcard)
            {
                Found = true;
            }
        }
    }
}
