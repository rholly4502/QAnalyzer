using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi klausa <c>SELECT TOP</c> tanpa <c>ORDER BY</c> yang menghasilkan baris tidak deterministik,
/// serta anti-pattern <c>TOP (100) PERCENT ... ORDER BY</c> di dalam view, CTE, subquery, atau inline function
/// yang urutannya diabaikan oleh query optimizer SQL Server.
/// </summary>
public sealed class TopWithoutOrderByRule : IAstSqlRule
{
    private static readonly HashSet<string> AggregateFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT",
        "COUNT_BIG",
        "SUM",
        "AVG",
        "MIN",
        "MAX",
        "STRING_AGG",
        "CHECKSUM_AGG",
        "STDEV",
        "STDEVP",
        "VAR",
        "VARP",
        "GROUPING",
        "GROUPING_ID",
        "APPROX_COUNT_DISTINCT",
    };

    public string Id => "TOP_WITHOUT_ORDER_BY";
    public string Title => "SELECT TOP tanpa ORDER BY deterministik";
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
        var visitor = new TopWithoutOrderByVisitor(Level, Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics
            .OrderBy(static d => d.Line)
            .ThenBy(static d => d.Column);
    }

    private sealed class TopWithoutOrderByVisitor(RiskLevel defaultLevel, string ruleId, string title) : TSqlFragmentVisitor
    {
        private readonly HashSet<QuerySpecification> _existsQueries = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SelectStatement> _nonStandaloneSelectStatements = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<QuerySpecification> _standaloneTopLevelSpecs = new(ReferenceEqualityComparer.Instance);

        public List<Diagnostic> Diagnostics { get; } = [];

        public override void ExplicitVisit(CreateViewStatement node) => MarkEmbeddedSelect(node, node.SelectStatement);

        public override void ExplicitVisit(AlterViewStatement node) => MarkEmbeddedSelect(node, node.SelectStatement);

        public override void ExplicitVisit(CreateOrAlterViewStatement node) => MarkEmbeddedSelect(node, node.SelectStatement);

        public override void ExplicitVisit(SelectFunctionReturnType node) => MarkEmbeddedSelect(node, node.SelectStatement);

        public override void ExplicitVisit(CursorDefinition node) => MarkEmbeddedSelect(node, node.Select);

        public override void ExplicitVisit(SelectStatement node)
        {
            if (node.Into is null
                && !_nonStandaloneSelectStatements.Contains(node)
                && UnwrapSingleQuerySpecification(node.QueryExpression) is { } topSpec)
            {
                _standaloneTopLevelSpecs.Add(topSpec);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(ExistsPredicate node)
        {
            foreach (var spec in SqlAstParser.GetDirectQuerySpecifications(node.Subquery?.QueryExpression))
            {
                _existsQueries.Add(spec);
            }

            node.AcceptChildren(this);
        }

        public override void Visit(QuerySpecification node)
        {
            if (node.TopRowFilter is not { } top)
            {
                return;
            }

            if (IsHundredPercentTop(top))
            {
                if (node.OrderByClause is not null
                    && node.ForClause is null
                    && !_standaloneTopLevelSpecs.Contains(node))
                {
                    AddDiagnostic(
                        "Klausa TOP (100) PERCENT ... ORDER BY di dalam view, CTE, subquery, atau inline function diabaikan oleh query optimizer SQL Server dan tidak menjamin urutan hasil.",
                        "Hapus TOP (100) PERCENT beserta ORDER BY dari definisi dalam, lalu tempatkan klausa ORDER BY pada query SELECT terluar.",
                        top);
                }

                return;
            }

            if (node.OrderByClause is not null
                || _existsQueries.Contains(node)
                || node.FromClause is null
                || (node.GroupByClause is null && IsSingleRowAggregateQuery(node.SelectElements))
                || (IsTopOne(top) && AreAllSelectElementsConstantOrVariable(node.SelectElements)))
            {
                return;
            }

            AddDiagnostic(
                "Klausa SELECT TOP tanpa ORDER BY menghasilkan baris yang tidak deterministik (bergantung pada urutan fisik, index yang dipilih optimizer, atau paralelisme).",
                "Tambahkan klausa ORDER BY dengan kolom penentu urutan yang jelas, atau gunakan EXISTS bila hanya memeriksa keberadaan data.",
                top);
        }

        private void MarkEmbeddedSelect(TSqlFragment container, SelectStatement? selectStatement)
        {
            if (selectStatement is not null)
            {
                _nonStandaloneSelectStatements.Add(selectStatement);
            }

            container.AcceptChildren(this);
        }

        private void AddDiagnostic(string message, string suggestion, TSqlFragment anchor) =>
            Diagnostics.Add(new Diagnostic(
                defaultLevel,
                ruleId,
                title,
                message,
                suggestion,
                anchor.StartLine,
                anchor.StartColumn));
    }

    private static QuerySpecification? UnwrapSingleQuerySpecification(QueryExpression? expression)
    {
        while (expression is QueryParenthesisExpression paren && paren.OrderByClause is null)
        {
            expression = paren.QueryExpression;
        }

        return expression as QuerySpecification;
    }

    private static bool IsTopOne(TopRowFilter top) =>
        !top.Percent
        && Unwrap(top.Expression) is IntegerLiteral lit
        && int.TryParse(lit.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
        && count == 1;

    private static bool IsHundredPercentTop(TopRowFilter top) =>
        top.Percent
        && Unwrap(top.Expression) is Literal { Value: { } raw }
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
        && pct == 100.0;

    private static bool AreAllSelectElementsConstantOrVariable(IList<SelectElement> elements) =>
        elements.Count > 0 && elements.All(static el => el switch
        {
            SelectScalarExpression scalar => IsConstantOrVariableExpression(scalar.Expression),
            SelectSetVariable setVar => IsConstantOrVariableExpression(setVar.Expression),
            _ => false,
        });

    private static bool IsConstantOrVariableExpression(ScalarExpression? expression) =>
        Unwrap(expression) switch
        {
            Literal or VariableReference => true,
            UnaryExpression unary => IsConstantOrVariableExpression(unary.Expression),
            CastCall cast => IsConstantOrVariableExpression(cast.Parameter),
            ConvertCall convert => IsConstantOrVariableExpression(convert.Parameter),
            TryCastCall tryCast => IsConstantOrVariableExpression(tryCast.Parameter),
            TryConvertCall tryConvert => IsConstantOrVariableExpression(tryConvert.Parameter),
            _ => false,
        };

    private static bool IsSingleRowAggregateQuery(IList<SelectElement> elements)
    {
        if (elements.Count == 0)
        {
            return false;
        }

        var hasAggregate = false;
        foreach (var element in elements)
        {
            var expr = element switch
            {
                SelectScalarExpression scalar => scalar.Expression,
                SelectSetVariable setVar => setVar.Expression,
                _ => null,
            };

            if (expr is null)
            {
                return false;
            }

            var inspector = new AggregateProjectionInspector();
            expr.Accept(inspector);
            if (inspector.HasUnaggregatedColumn)
            {
                return false;
            }

            hasAggregate |= inspector.HasAggregate;
        }

        return hasAggregate;
    }

    private static ScalarExpression? Unwrap(ScalarExpression? expression)
    {
        while (expression is ParenthesisExpression paren)
        {
            expression = paren.Expression;
        }

        return expression;
    }

    private sealed class AggregateProjectionInspector : TSqlFragmentVisitor
    {
        public bool HasAggregate { get; private set; }
        public bool HasUnaggregatedColumn { get; private set; }

        public override void ExplicitVisit(ScalarSubquery node) { }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is null
                && node.OverClause is null
                && AggregateFunctions.Contains(node.FunctionName.Value))
            {
                HasAggregate = true;
                return;
            }

            node.AcceptChildren(this);
        }

        public override void Visit(ColumnReferenceExpression node) => HasUnaggregatedColumn = true;
    }
}
