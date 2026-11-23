using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi <c>INSERT</c>, <c>MERGE ... THEN INSERT</c>, dan <c>OUTPUT ... INTO</c> tanpa daftar kolom
/// tujuan eksplisit via AST ScriptDom yang membuat query bergantung pada urutan fisik kolom tabel
/// serta rentan gagal atau salah penempatan data saat skema berubah.
/// </summary>
public sealed class InsertWithoutColumnListRule : IAstSqlRule
{
    public string Id => "INSERT_WITHOUT_COLUMNS";
    public string Title => "INSERT tanpa daftar kolom eksplisit";
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
        var visitor = new InsertWithoutColumnListVisitor(Level, Id, Title);
        fragment.Accept(visitor);
        return visitor.Diagnostics
            .OrderBy(static d => d.Line)
            .ThenBy(static d => d.Column);
    }

    private sealed class InsertWithoutColumnListVisitor(RiskLevel defaultLevel, string ruleId, string title) : TSqlFragmentVisitor
    {
        public List<Diagnostic> Diagnostics { get; } = [];

        public override void Visit(InsertSpecification node)
        {
            if (node.Target is null
                || node.InsertSource is null
                || node.Columns.Count > 0
                || node.InsertSource is ValuesInsertSource { IsDefaultValues: true })
            {
                return;
            }

            var target = FormatTargetName(node.Target);
            var targetLabel = string.IsNullOrEmpty(target) ? "INSERT" : $"INSERT ke {target}";

            AddDiagnostic(
                $"{targetLabel} tanpa daftar kolom eksplisit bergantung pada urutan fisik kolom tabel — penambahan, penghapusan, atau perubahan urutan kolom akan memicu error runtime atau salah penempatan data.",
                "Sebutkan daftar kolom tujuan secara eksplisit pada klausa INSERT, misalnya INSERT INTO Tabel (Kolom1, Kolom2) ...",
                node);
        }

        public override void Visit(InsertMergeAction node)
        {
            if (node.Source is null
                || node.Columns.Count > 0
                || node.Source.IsDefaultValues)
            {
                return;
            }

            AddDiagnostic(
                "Klausa INSERT pada MERGE tanpa daftar kolom eksplisit bergantung pada urutan fisik seluruh kolom tabel target dan rentan gagal saat skema berubah.",
                "Sebutkan daftar kolom tujuan secara eksplisit pada klausa MERGE, misalnya WHEN NOT MATCHED THEN INSERT (Kolom1, Kolom2) VALUES (...).",
                node);
        }

        public override void Visit(OutputIntoClause node)
        {
            if (node.IntoTable is null || node.IntoTableColumns.Count > 0)
            {
                return;
            }

            var target = FormatTargetName(node.IntoTable);
            var targetLabel = string.IsNullOrEmpty(target) ? "Klausa OUTPUT INTO" : $"Klausa OUTPUT INTO {target}";

            AddDiagnostic(
                $"{targetLabel} tanpa daftar kolom tujuan bergantung pada urutan fisik kolom tabel target dan rentan gagal saat skema berubah.",
                "Sebutkan daftar kolom tujuan secara eksplisit pada klausa OUTPUT INTO, misalnya OUTPUT ... INTO TabelTujuan (Kolom1, Kolom2).",
                node);
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

    private static string FormatTargetName(TableReference? target) =>
        target switch
        {
            NamedTableReference { SchemaObject.Identifiers: { Count: > 0 } ids } =>
                string.Join(".", ids.Select(static id => id.Value)),
            VariableTableReference { Variable.Name: { Length: > 0 } varName } =>
                varName,
            _ => string.Empty,
        };
}
