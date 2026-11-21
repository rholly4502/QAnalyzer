using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>Deteksi `SELECT *` via AST ScriptDom (boros I/O; saran: kolom eksplisit).</summary>
public sealed class SelectStarRule : IAstSqlRule
{
    public string Id => "SELECT_STAR";
    public string Title => "SELECT * tanpa kolom eksplisit";
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
        var visitor = new SelectStarVisitor();
        fragment.Accept(visitor);

        return visitor.Matches.Select(node => new Diagnostic(
            Level,
            Id,
            Title,
            "SELECT * mengambil semua kolom — boros I/O dan rentan bila skema berubah.",
            "Tulis kolom yang dibutuhkan secara eksplisit.",
            node.StartLine,
            node.StartColumn));
    }

    private sealed class SelectStarVisitor : TSqlFragmentVisitor
    {
        private readonly HashSet<SelectStarExpression> _ignoredStars = [];

        public List<SelectStarExpression> Matches { get; } = [];

        public override void ExplicitVisit(ExistsPredicate node)
        {
            // SELECT * di dalam EXISTS tidak mengembalikan data kolom, jadi bukan anti-pattern boros I/O.
            foreach (var spec in SqlAstParser.GetDirectQuerySpecifications(node.Subquery?.QueryExpression))
            {
                foreach (var star in spec.SelectElements.OfType<SelectStarExpression>())
                {
                    _ignoredStars.Add(star);
                }
            }

            node.AcceptChildren(this);
        }

        public override void Visit(SelectStarExpression node)
        {
            if (!_ignoredStars.Contains(node))
            {
                Matches.Add(node);
            }
        }
    }
}
