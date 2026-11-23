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

        public override void Visit(MergeActionClause node) => InspectBooleanExpression(node.SearchCondition);

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
                    if (!ContainsDirectColumnReference(inPred.Expression))
                    {
                        foreach (var value in inPred.Values)
                        {
                            InspectOperand(value, allowArithmeticOnColumn: true);
                        }
                    }
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

            var lhsColCount = CountDirectColumnReferences(lhs);
            var rhsColCount = CountDirectColumnReferences(rhs);
            if (lhsColCount == 0 && rhsColCount == 0)
            {
                return;
            }

            InspectOperand(lhs, allowArithmeticOnColumn: rhsColCount == 0 || rhs is not ColumnReferenceExpression || lhsColCount == 1);
            InspectOperand(rhs, allowArithmeticOnColumn: lhsColCount == 0 || lhs is not ColumnReferenceExpression || rhsColCount == 1);
        }

        private void InspectLike(LikePredicate like)
        {
            InspectOperand(like.FirstExpression, allowArithmeticOnColumn: true);

            if (ContainsDirectColumnReference(like.FirstExpression)
                && HasLeadingWildcard(like.SecondExpression, like.EscapeExpression))
            {
                AddDiagnostic(
                    "Pola LIKE diawali wildcard ('%', '_', atau '[...]') pada kolom — mencegah Index Seek dan memaksa Index/Table Scan.",
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

            if (unwrapped is FunctionCall or LeftFunctionCall or RightFunctionCall or OdbcFunctionCall
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
        char? escapeChar = TryGetLeadingStringLiteral(escapeExpr) is { Length: 1 } escText
            ? escText[0]
            : null;

        var text = TryGetLeadingStringLiteral(patternExpr);
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var first = text[0];
        if (escapeChar is not null && first == escapeChar.Value)
        {
            return false;
        }

        if (first is '%' or '_')
        {
            return true;
        }

        // Di T-SQL LIKE, '[x]' (1 karakter) adalah literal escape (mis. '[%]' atau '[_]'),
        // sedangkan '[a-z]', '[ab]', atau '[^0-9]' (isi >= 2 karakter) adalah wildcard kelas karakter.
        return first == '[' && text.IndexOf(']', 1) > 2;
    }

    private static string? TryGetLeadingStringLiteral(ScalarExpression? expression) =>
        Unwrap(expression) switch
        {
            StringLiteral lit => lit.Value,
            CastCall cast => TryGetLeadingStringLiteral(cast.Parameter),
            ConvertCall convert => TryGetLeadingStringLiteral(convert.Parameter),
            TryCastCall tryCast => TryGetLeadingStringLiteral(tryCast.Parameter),
            TryConvertCall tryConvert => TryGetLeadingStringLiteral(tryConvert.Parameter),
            BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add or BinaryExpressionType.Concat } bin
                => TryGetLeadingFromSequence([bin.FirstExpression, bin.SecondExpression]),
            FunctionCall fn when fn.CallTarget is null
                && fn.FunctionName.Value.Equals("CONCAT", StringComparison.OrdinalIgnoreCase)
                && fn.Parameters.Count > 0
                => TryGetLeadingFromSequence(fn.Parameters),
            _ => null,
        };

    private static string? TryGetLeadingFromSequence(IEnumerable<ScalarExpression?> expressions)
    {
        var sawEmptyLiteral = false;
        foreach (var expr in expressions)
        {
            var leading = TryGetLeadingStringLiteral(expr);
            if (leading is null)
            {
                return null;
            }

            if (leading.Length > 0)
            {
                return leading;
            }

            sawEmptyLiteral = true;
        }

        return sawEmptyLiteral ? string.Empty : null;
    }

    private static bool ContainsDirectColumnReference(TSqlFragment? node) =>
        CountDirectColumnReferences(node) > 0;

    private static int CountDirectColumnReferences(TSqlFragment? node)
    {
        if (node is null)
        {
            return 0;
        }

        var finder = new DirectColumnFinder();
        node.Accept(finder);
        return finder.Count;
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

        public int Count { get; private set; }

        public override void ExplicitVisit(ScalarSubquery node)
        {
            // Jangan ikutkan referensi kolom di dalam subquery skalar ke ekspresi luar.
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is null && DatePartFunctions.Contains(node.FunctionName.Value))
            {
                // Parameter pertama fungsi tanggal (DAY, MONTH, YEAR, dll.) di-parse sebagai ColumnReferenceExpression.
                for (var i = 1; i < node.Parameters.Count; i++)
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
                Count++;
            }
        }
    }
}
