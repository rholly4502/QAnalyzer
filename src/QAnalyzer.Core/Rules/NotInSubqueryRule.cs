using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi predicate <c>NOT IN</c> via AST ScriptDom yang berisiko jebakan <c>NULL</c>
/// (three-valued logic menghasilkan 0 baris) maupun anti-pattern performa Anti Semi Join / Spool.
/// </summary>
public sealed class NotInSubqueryRule : IAstSqlRule
{
    public string Id => "NOT_IN_SUBQUERY";
    public string Title => "Predicate NOT IN berisiko jebakan NULL";
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
        var visitor = new NotInVisitor(Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics;
    }

    private sealed class NotInVisitor(string ruleId, string title) : TSqlFragmentVisitor
    {
        public List<Diagnostic> Diagnostics { get; } = [];

        public override void ExplicitVisit(BooleanNotExpression node)
        {
            if (UnwrapBooleanNots(node, out var notCount) is InPredicate inPred)
            {
                if (((notCount % 2) == 1) ^ inPred.NotDefined) InspectNegatedIn(inPred, node);
                // Lanjutkan ke child InPredicate (mis. subquery bersarang) tanpa mengunjungi ulang InPredicate itu sendiri.
                inPred.AcceptChildren(this);
                return;
            }
            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(InPredicate node)
        {
            if (node.NotDefined) InspectNegatedIn(node, node);
            node.AcceptChildren(this);
        }

        private void InspectNegatedIn(InPredicate inPred, TSqlFragment anchor)
        {
            if (inPred.Subquery is not null)
            {
                if (!IsQueryGuaranteedNonNull(inPred.Subquery.QueryExpression))
                {
                    AddDiagnostic(
                        RiskLevel.Warning,
                        "Predicate NOT IN dengan subquery yang berpotensi mengembalikan NULL dapat menghasilkan 0 baris (three-valued logic) dan memicu rencana Anti Semi Join/Spool yang lambat.",
                        "Ganti dengan NOT EXISTS (lebih aman terhadap NULL dan umumnya menghasilkan plan lebih baik), atau pastikan kolom subquery disaring dengan IS NOT NULL.",
                        anchor);
                }
                return;
            }

            if (inPred.Values.Any(IsDefinitelyNull))
            {
                AddDiagnostic(
                    RiskLevel.Critical,
                    "Daftar nilai pada NOT IN memuat NULL secara eksplisit — pada logika tiga-nilai SQL (three-valued logic), predicate ini selalu bernilai UNKNOWN dan menghasilkan 0 baris.",
                    "Hapus nilai NULL dari daftar NOT IN dan tangani kondisi NULL secara eksplisit dengan IS NOT NULL bila diperlukan.",
                    anchor);
            }
        }

        private void AddDiagnostic(RiskLevel level, string message, string suggestion, TSqlFragment anchor) =>
            Diagnostics.Add(new Diagnostic(level, ruleId, title, message, suggestion, anchor.StartLine, anchor.StartColumn));
    }

    private static bool IsDefinitelyNull(ScalarExpression? expr) =>
        Unwrap(expr) switch
        {
            NullLiteral => true,
            CastCall c => IsDefinitelyNull(c.Parameter),
            ConvertCall c => IsDefinitelyNull(c.Parameter),
            TryCastCall c => IsDefinitelyNull(c.Parameter),
            TryConvertCall c => IsDefinitelyNull(c.Parameter),
            _ => false,
        };

    private static bool IsQueryGuaranteedNonNull(QueryExpression? expr) =>
        expr switch
        {
            QueryParenthesisExpression paren => IsQueryGuaranteedNonNull(paren.QueryExpression),
            BinaryQueryExpression bin => bin.BinaryQueryExpressionType switch
            {
                BinaryQueryExpressionType.Union => IsQueryGuaranteedNonNull(bin.FirstQueryExpression)
                    && IsQueryGuaranteedNonNull(bin.SecondQueryExpression),
                BinaryQueryExpressionType.Except => IsQueryGuaranteedNonNull(bin.FirstQueryExpression),
                BinaryQueryExpressionType.Intersect => IsQueryGuaranteedNonNull(bin.FirstQueryExpression)
                    || IsQueryGuaranteedNonNull(bin.SecondQueryExpression),
                _ => false,
            },
            QuerySpecification spec => IsQuerySpecGuaranteedNonNull(spec),
            _ => false,
        };

    private static bool IsQuerySpecGuaranteedNonNull(QuerySpecification spec)
    {
        if (spec.SelectElements.Count != 1 || spec.SelectElements[0] is not SelectScalarExpression scalar) return false;
        var projected = Unwrap(scalar.Expression);
        if (IsExpressionGuaranteedNonNull(projected)) return true;
        if (projected is not ColumnReferenceExpression { ColumnType: not ColumnType.Wildcard } colRef) return false;

        var singleTable = TryGetSingleTableQualifier(spec.FromClause);
        return (CanWhereGuaranteeNonNull(spec.GroupByClause)
                && IsNullRejectingPredicate(spec.WhereClause?.SearchCondition, colRef, singleTable))
            || IsNullRejectingPredicate(spec.HavingClause?.SearchCondition, colRef, singleTable);
    }

    private static bool CanWhereGuaranteeNonNull(GroupByClause? groupBy) =>
        groupBy is null
        || (groupBy.GroupByOption == GroupByOption.None
            && !groupBy.All
            && groupBy.GroupingSpecifications.All(static g => g is ExpressionGroupingSpecification));

    private static bool IsExpressionGuaranteedNonNull(ScalarExpression? expr) =>
        Unwrap(expr) switch
        {
            Literal lit => lit is not (NullLiteral or DefaultLiteral)
                && lit.LiteralType is not (LiteralType.Null or LiteralType.Default),
            UnaryExpression unary => IsExpressionGuaranteedNonNull(unary.Expression),
            CastCall cast => IsExpressionGuaranteedNonNull(cast.Parameter),
            ConvertCall convert => IsExpressionGuaranteedNonNull(convert.Parameter),
            CoalesceExpression coalesce => coalesce.Expressions.Any(IsExpressionGuaranteedNonNull),
            IIfCall iif => IsExpressionGuaranteedNonNull(iif.ThenExpression)
                && IsExpressionGuaranteedNonNull(iif.ElseExpression),
            SimpleCaseExpression sc => sc.ElseExpression is not null
                && IsExpressionGuaranteedNonNull(sc.ElseExpression)
                && sc.WhenClauses.All(w => IsExpressionGuaranteedNonNull(w.ThenExpression)),
            SearchedCaseExpression sc => sc.ElseExpression is not null
                && IsExpressionGuaranteedNonNull(sc.ElseExpression)
                && sc.WhenClauses.All(w => IsExpressionGuaranteedNonNull(w.ThenExpression)),
            FunctionCall fn when fn.CallTarget is null => IsFunctionGuaranteedNonNull(fn),
            _ => false,
        };

    private static bool IsFunctionGuaranteedNonNull(FunctionCall fn)
    {
        var name = fn.FunctionName.Value;
        if (name.Equals("ISNULL", StringComparison.OrdinalIgnoreCase))
            return fn.Parameters.Count == 2 && (IsExpressionGuaranteedNonNull(fn.Parameters[0]) || IsExpressionGuaranteedNonNull(fn.Parameters[1]));
        if (name.Equals("COUNT", StringComparison.OrdinalIgnoreCase) || name.Equals("COUNT_BIG", StringComparison.OrdinalIgnoreCase))
            return true;
        return fn.OverClause is not null
            && (name.Equals("ROW_NUMBER", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RANK", StringComparison.OrdinalIgnoreCase)
                || name.Equals("DENSE_RANK", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NTILE", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNullRejectingPredicate(BooleanExpression? expr, ColumnReferenceExpression projected, SingleTableQualifier? singleTable) =>
        expr switch
        {
            null => false,
            BooleanParenthesisExpression paren => IsNullRejectingPredicate(paren.Expression, projected, singleTable),
            BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } andExpr =>
                IsNullRejectingPredicate(andExpr.FirstExpression, projected, singleTable)
                || IsNullRejectingPredicate(andExpr.SecondExpression, projected, singleTable),
            BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } orExpr =>
                IsNullRejectingPredicate(orExpr.FirstExpression, projected, singleTable)
                && IsNullRejectingPredicate(orExpr.SecondExpression, projected, singleTable),
            BooleanIsNullExpression isNull => isNull.IsNot && MatchesProjectedColumn(isNull.Expression, projected, singleTable),
            BooleanNotExpression notExpr => UnwrapBooleanNots(notExpr, out var notCount) is BooleanIsNullExpression isNull
                && (((notCount % 2) == 1) ^ isNull.IsNot)
                && MatchesProjectedColumn(isNull.Expression, projected, singleTable),
            BooleanComparisonExpression comp when comp.ComparisonType is not (BooleanComparisonType.IsDistinctFrom
                or BooleanComparisonType.IsNotDistinctFrom
                or BooleanComparisonType.LeftOuterJoin
                or BooleanComparisonType.RightOuterJoin) =>
                MatchesProjectedColumn(comp.FirstExpression, projected, singleTable)
                || MatchesProjectedColumn(comp.SecondExpression, projected, singleTable),
            BooleanTernaryExpression ternary => MatchesProjectedColumn(ternary.FirstExpression, projected, singleTable),
            LikePredicate like => MatchesProjectedColumn(like.FirstExpression, projected, singleTable),
            InPredicate inPred => MatchesProjectedColumn(inPred.Expression, projected, singleTable),
            _ => false,
        };

    private static BooleanExpression? UnwrapBooleanNots(BooleanExpression? current, out int notCount)
    {
        notCount = 0;
        while (current is BooleanNotExpression or BooleanParenthesisExpression)
        {
            if (current is BooleanNotExpression notExpr) { notCount++; current = notExpr.Expression; }
            else if (current is BooleanParenthesisExpression paren) current = paren.Expression;
        }
        return current;
    }

    private static bool MatchesProjectedColumn(ScalarExpression? expr, ColumnReferenceExpression projected, SingleTableQualifier? singleTable) =>
        Unwrap(expr) is ColumnReferenceExpression { ColumnType: not ColumnType.Wildcard } candidate
        && ColumnsMatch(projected, candidate, singleTable);

    private static bool ColumnsMatch(ColumnReferenceExpression projected, ColumnReferenceExpression candidate, SingleTableQualifier? singleTable)
    {
        var projIds = projected.MultiPartIdentifier?.Identifiers;
        var candIds = candidate.MultiPartIdentifier?.Identifiers;
        if (projIds is null || candIds is null || projIds.Count == 0 || candIds.Count == 0) return false;
        if (!string.Equals(projIds[^1].Value, candIds[^1].Value, StringComparison.OrdinalIgnoreCase)) return false;
        if (projIds.Count == 1 && candIds.Count == 1) return true;

        if (projIds.Count > 1 && candIds.Count > 1)
        {
            return QualifiersMatchExact(projIds, candIds)
                || (singleTable is not null && singleTable.MatchesQualifier(projIds) && singleTable.MatchesQualifier(candIds));
        }

        var qualifiedIds = projIds.Count > 1 ? projIds : candIds;
        return singleTable is not null && singleTable.MatchesQualifier(qualifiedIds);
    }

    private static bool QualifiersMatchExact(IList<Identifier> left, IList<Identifier> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count - 1; i++)
        {
            if (!string.Equals(left[i].Value, right[i].Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private static SingleTableQualifier? TryGetSingleTableQualifier(FromClause? fromClause)
    {
        if (fromClause?.TableReferences is not { Count: 1 } refs || refs[0] is not TableReferenceWithAlias twa) return null;
        if (twa.Alias is not null) return new SingleTableQualifier([twa.Alias.Value], allowSuffixMatch: false);
        if (twa is NamedTableReference named && named.SchemaObject?.Identifiers is { Count: > 0 } ids)
            return new SingleTableQualifier(ids.Select(id => id.Value).ToArray(), allowSuffixMatch: true);
        if (twa is VariableTableReference varRef && varRef.Variable is not null)
            return new SingleTableQualifier([varRef.Variable.Name], allowSuffixMatch: false);
        return null;
    }

    private static ScalarExpression? Unwrap(ScalarExpression? expression)
    {
        while (expression is ParenthesisExpression paren) expression = paren.Expression;
        return expression;
    }

    private sealed class SingleTableQualifier(IReadOnlyList<string> tableParts, bool allowSuffixMatch)
    {
        public bool MatchesQualifier(IList<Identifier> columnIdentifiers)
        {
            var qualifierLen = columnIdentifiers.Count - 1;
            if (qualifierLen <= 0 || qualifierLen > tableParts.Count) return false;
            if (!allowSuffixMatch && qualifierLen != tableParts.Count) return false;

            var offset = tableParts.Count - qualifierLen;
            for (var i = 0; i < qualifierLen; i++)
            {
                if (!string.Equals(columnIdentifiers[i].Value, tableParts[offset + i], StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
    }
}
