using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core;

/// <summary>
/// Guard berbasis AST ScriptDom dan normalisasi pola plan cache agar pengambilan
/// execution plan lokal tidak pernah mengeksekusi mutasi data/skema.
/// </summary>
public static partial class ExecutionPlanQueryGuard
{
    private const int MaxCachePatternLength = 250;

    [GeneratedRegex(@"N?'(?:''|[^'])*'|\b\d+(?:\.\d+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex LiteralRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("\0+")]
    private static partial Regex SentinelCollapseRegex();

    /// <summary>
    /// Memastikan script T-SQL hanya berisi satu batch read-only (<c>SELECT</c> beserta
    /// opsional <c>DECLARE</c>/<c>SET</c> variabel) tanpa mutasi data, DDL, atau eksekusi prosedur.
    /// </summary>
    public static bool CanSafelyEstimatePlan(string sql, out string? rejectionReason)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return Fail("Script SQL kosong.", out rejectionReason);

        var fragment = SqlAstParser.Parse(sql, out var errors);
        if (fragment is not TSqlScript script || errors.Count > 0)
            return Fail("Script T-SQL tidak valid atau mengandung syntax error.", out rejectionReason);

        if (script.Batches.Count != 1)
            return Fail("Estimated plan hanya mendukung satu batch T-SQL tanpa pemisah GO.", out rejectionReason);

        var statements = script.Batches[0].Statements;
        if (statements.Count == 0)
            return Fail("Script T-SQL tidak memiliki statement.", out rejectionReason);

        var hasSelect = false;
        foreach (var stmt in statements)
        {
            if (stmt is SelectStatement s)
            {
                if (s.Into is not null)
                    return Fail("SELECT ... INTO memodifikasi skema/data dan tidak diizinkan.", out rejectionReason);
                hasSelect = true;
            }
            else if (stmt is not (DeclareVariableStatement or SetVariableStatement))
            {
                return Fail($"Statement '{stmt.GetType().Name}' tidak diizinkan untuk estimated plan otomatis.", out rejectionReason);
            }
        }

        if (!hasSelect)
            return Fail("Estimated plan membutuhkan minimal satu statement SELECT.", out rejectionReason);

        var visitor = new UnsafeNodeVisitor();
        script.Accept(visitor);
        if (visitor.RejectionReason is not null)
            return Fail(visitor.RejectionReason, out rejectionReason);

        rejectionReason = null;
        return true;
    }

    /// <summary>
    /// Membangun pola <c>LIKE ... ESCAPE '\'</c> yang aman untuk pencarian teks query
    /// di <c>sys.dm_exec_sql_text</c> tanpa wildcard injection.
    /// </summary>
    public static string BuildPlanCacheLikePattern(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText)) return string.Empty;

        var sanitized = sqlText.Replace("\0", string.Empty, StringComparison.Ordinal);
        var collapsed = SentinelCollapseRegex().Replace(LiteralRegex().Replace(sanitized, "\0"), "\0");
        var normalized = WhitespaceRegex().Replace(collapsed, " ").Trim().TrimEnd(';').Trim();
        if (normalized.Count(c => c != '\0' && !char.IsWhiteSpace(c)) < 3) return string.Empty;

        if (normalized.Length > MaxCachePatternLength) normalized = normalized[..MaxCachePatternLength];
        var startsWild = normalized.StartsWith('\0');
        var endsWild = normalized.EndsWith('\0');

        var escaped = normalized
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal)
            .Replace("[", @"\[", StringComparison.Ordinal)
            .Replace("\0", "%", StringComparison.Ordinal);

        return (startsWild ? string.Empty : "%") + escaped + (endsWild ? string.Empty : "%");
    }

    private static bool Fail(string message, out string? reason) { reason = message; return false; }

    private sealed class UnsafeNodeVisitor : TSqlFragmentVisitor
    {
        public string? RejectionReason { get; private set; }
        public override void Visit(DataModificationTableReference node) => RejectionReason ??= "Referensi DML di dalam query tidak diizinkan.";
        public override void Visit(DataModificationStatement node) => RejectionReason ??= "Operasi modifikasi data tidak diizinkan.";
        public override void Visit(ExecuteSpecification node) => RejectionReason ??= "Eksekusi prosedur/SQL dinamis tidak diizinkan.";
        public override void Visit(PredicateSetStatement node) => RejectionReason ??= "Statement SET sesi tidak diizinkan.";
        public override void Visit(OpenRowsetTableReference node) => RejectionReason ??= "Akses OPENROWSET tidak diizinkan.";
        public override void Visit(AdHocTableReference node) => RejectionReason ??= "Akses ad-hoc OPENDATASOURCE tidak diizinkan.";
        public override void Visit(BulkOpenRowset node) => RejectionReason ??= "Akses BULK OPENROWSET tidak diizinkan.";
        public override void Visit(OpenQueryTableReference node) => RejectionReason ??= "Akses OPENQUERY tidak diizinkan.";
        public override void Visit(SelectStatement node) { if (node.Into is not null) RejectionReason ??= "SELECT ... INTO memodifikasi skema/data dan tidak diizinkan."; }
    }
}
