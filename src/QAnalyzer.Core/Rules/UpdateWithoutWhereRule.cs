using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi `UPDATE` tanpa `WHERE` via AST ScriptDom (risiko update massal tanpa rollback).
/// </summary>
public sealed class UpdateWithoutWhereRule : IAstSqlRule
{
    public string Id => "UPDATE_NO_WHERE";
    public string Title => "UPDATE tanpa klausa WHERE";
    public RiskLevel Level => RiskLevel.Critical;

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
        var visitor = new UpdateWithoutWhereVisitor();
        fragment.Accept(visitor);

        return visitor.Matches.Select(node => new Diagnostic(
            Level,
            Id,
            Title,
            "UPDATE tanpa WHERE akan mengubah SEMUA baris — risiko massal tanpa rollback.",
            "Tambahkan klausa WHERE; untuk batch production, gunakan log table + batch + PREVIEW.",
            node.StartLine,
            node.StartColumn));
    }

    private sealed class UpdateWithoutWhereVisitor : TSqlFragmentVisitor
    {
        public List<UpdateSpecification> Matches { get; } = [];

        public override void Visit(UpdateSpecification node)
        {
            if (node.WhereClause is null)
            {
                Matches.Add(node);
            }
        }
    }
}
