using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi `DELETE` tanpa `WHERE` via AST ScriptDom (risiko hapus massal tanpa rollback).
/// </summary>
public sealed class DeleteWithoutWhereRule : IAstSqlRule
{
    public string Id => "DELETE_NO_WHERE";
    public string Title => "DELETE tanpa klausa WHERE";
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
        var visitor = new DeleteWithoutWhereVisitor();
        fragment.Accept(visitor);

        return visitor.Matches.Select(node => new Diagnostic(
            Level,
            Id,
            Title,
            "DELETE tanpa WHERE akan menghapus SEMUA baris — risiko hapus massal tanpa rollback.",
            "Tambahkan klausa WHERE; untuk penghapusan massal di production, lakukan bertahap (batch) dengan backup/verifikasi terlebih dahulu.",
            node.StartLine,
            node.StartColumn));
    }

    private sealed class DeleteWithoutWhereVisitor : TSqlFragmentVisitor
    {
        public List<DeleteSpecification> Matches { get; } = [];

        public override void Visit(DeleteSpecification node)
        {
            if (node.WhereClause is null)
            {
                Matches.Add(node);
            }
        }
    }
}
