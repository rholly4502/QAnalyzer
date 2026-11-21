using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi statement destruktif `TRUNCATE TABLE` via AST ScriptDom.
/// </summary>
public sealed class TruncateTableRule : IAstSqlRule
{
    public string Id => "TRUNCATE_TABLE";
    public string Title => "TRUNCATE TABLE destruktif";
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
        var visitor = new TruncateTableVisitor();
        fragment.Accept(visitor);

        return visitor.Matches.Select(node =>
        {
            var table = FormatTableName(node.TableName);
            var target = string.IsNullOrEmpty(table) ? "TRUNCATE TABLE" : $"TRUNCATE TABLE {table}";
            var message = node.PartitionRanges?.Count > 0
                ? $"{target} (partisi tertentu) menghapus seluruh baris pada partisi secara minimally-logged dan berisiko kehilangan data."
                : $"{target} menghapus SEMUA baris secara permanen (minimally-logged, mereset identity) dan tidak bisa difilter dengan WHERE.";

            return new Diagnostic(
                Level,
                Id,
                Title,
                message,
                "Pastikan backup dan verifikasi target tabel tersedia; jika hanya menghapus sebagian data, gunakan DELETE dengan klausa WHERE dan transaksi terkontrol.",
                node.StartLine,
                node.StartColumn);
        });
    }

    private static string FormatTableName(SchemaObjectName? tableName) =>
        tableName?.Identifiers is { Count: > 0 }
            ? string.Join(".", tableName.Identifiers.Select(id => id.Value))
            : string.Empty;

    private sealed class TruncateTableVisitor : TSqlFragmentVisitor
    {
        public List<TruncateTableStatement> Matches { get; } = [];

        public override void Visit(TruncateTableStatement node) => Matches.Add(node);
    }
}
