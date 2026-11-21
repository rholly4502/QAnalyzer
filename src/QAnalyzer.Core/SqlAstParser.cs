using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core;

/// <summary>
/// Kontrak internal untuk rule statis berbasis AST ScriptDom agar satu script
/// hanya perlu di-parse sekali per panggilan <see cref="SqlAnalyzer.Analyze"/>.
/// </summary>
internal interface IAstSqlRule : ISqlRule
{
    IEnumerable<Diagnostic> Evaluate(TSqlFragment fragment);
}

/// <summary>
/// Helper internal pembungkus <see cref="TSql170Parser"/> agar tipe ScriptDom
/// tidak bocor ke API publik <c>QAnalyzer.Core</c>.
/// </summary>
internal static class SqlAstParser
{
    public static TSqlFragment? Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out _);
        return fragment;
    }

    public static IEnumerable<QuerySpecification> GetDirectQuerySpecifications(QueryExpression? expression)
    {
        switch (expression)
        {
            case QuerySpecification spec:
                yield return spec;
                break;
            case QueryParenthesisExpression paren:
                foreach (var inner in GetDirectQuerySpecifications(paren.QueryExpression))
                {
                    yield return inner;
                }
                break;
            case BinaryQueryExpression binary:
                foreach (var left in GetDirectQuerySpecifications(binary.FirstQueryExpression))
                {
                    yield return left;
                }
                foreach (var right in GetDirectQuerySpecifications(binary.SecondQueryExpression))
                {
                    yield return right;
                }
                break;
        }
    }
}
