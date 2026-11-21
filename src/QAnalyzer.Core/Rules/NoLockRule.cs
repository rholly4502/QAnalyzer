using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi `WITH (NOLOCK)` / `NOLOCK` / `READUNCOMMITTED` via AST ScriptDom.
/// Di SELECT polos normal (Info); naik Warning bila gabung SELECT *;
/// naik Critical bila di UPDATE/DELETE/INSERT/MERGE.
/// </summary>
public sealed class NoLockRule : IAstSqlRule
{
    public string Id => "NOLOCK";
    public string Title => "Hint NOLOCK digunakan";
    public RiskLevel Level => RiskLevel.Info;

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
        var visitor = new NoLockVisitor(Level, Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics;
    }

    private sealed class NoLockVisitor(RiskLevel defaultLevel, string ruleId, string title) : TSqlFragmentVisitor
    {
        private readonly Stack<bool> _selectStarStack = new();
        private readonly HashSet<QuerySpecification> _existsQueries = [];
        private int _writeDepth;

        public List<Diagnostic> Diagnostics { get; } = [];

        public override void ExplicitVisit(UpdateStatement node) => VisitWriteStatement(node);

        public override void ExplicitVisit(DeleteStatement node) => VisitWriteStatement(node);

        public override void ExplicitVisit(InsertStatement node) => VisitWriteStatement(node);

        public override void ExplicitVisit(MergeStatement node) => VisitWriteStatement(node);

        public override void ExplicitVisit(ExistsPredicate node)
        {
            foreach (var spec in SqlAstParser.GetDirectQuerySpecifications(node.Subquery?.QueryExpression))
            {
                _existsQueries.Add(spec);
            }

            node.AcceptChildren(this);
        }

        public override void ExplicitVisit(QuerySpecification node)
        {
            var hasSelectStar = !_existsQueries.Contains(node)
                && node.SelectElements.OfType<SelectStarExpression>().Any();
            _selectStarStack.Push(hasSelectStar);
            try
            {
                node.AcceptChildren(this);
            }
            finally
            {
                _selectStarStack.Pop();
            }
        }

        public override void Visit(TableHint node)
        {
            if (node.HintKind is not (TableHintKind.NoLock or TableHintKind.ReadUncommitted))
            {
                return;
            }

            var level = defaultLevel;
            var message = "Hint NOLOCK dipakai pada read query — normal untuk reporting/read-only.";
            var suggestion = "Pastikan query/index sudah di-tuning; NOLOCK bukan pengganti tuning.";

            if (_writeDepth > 0)
            {
                level = RiskLevel.Critical;
                message = "NOLOCK pada statement tulis (UPDATE/DELETE/INSERT) berbahaya — read-uncommitted pada data yang berubah.";
                suggestion = "Hapus NOLOCK pada statement tulis; gunakan isolasi yang tepat.";
            }
            else if (_selectStarStack.Count > 0 && _selectStarStack.Peek())
            {
                level = RiskLevel.Warning;
                message = "NOLOCK digabung SELECT * — boros I/O + risiko dirty read.";
                suggestion = "Pilih kolom eksplisit dan hindari NOLOCK tanpa alasan.";
            }

            Diagnostics.Add(new Diagnostic(
                level,
                ruleId,
                title,
                message,
                suggestion,
                node.StartLine,
                node.StartColumn));
        }

        private void VisitWriteStatement(TSqlStatement node)
        {
            _writeDepth++;
            try
            {
                node.AcceptChildren(this);
            }
            finally
            {
                _writeDepth--;
            }
        }
    }
}
