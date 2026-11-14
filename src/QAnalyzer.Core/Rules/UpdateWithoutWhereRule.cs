using System.Text.RegularExpressions;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi `UPDATE` tanpa `WHERE` (risiko update massal tanpa rollback).
/// Peringatan diabaikan bila penulis sengaja menyertakan log table / komentar,
/// tapi default tetap Critical.
/// </summary>
public sealed class UpdateWithoutWhereRule : ISqlRule
{
    public string Id => "UPDATE_NO_WHERE";
    public string Title => "UPDATE tanpa klausa WHERE";
    public RiskLevel Level => RiskLevel.Critical;

    private static readonly Regex UpdateRe = new(
        @"\bUPDATE\s+[a-zA-Z_""\[\]]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        foreach (Match m in UpdateRe.Matches(sql))
        {
            var start = m.Index + m.Length;
            var rest = sql[start..];
            // statement berakhir di ; atau di awal statement berikutnya (UPDATE/INSERT/DELETE/SELECT)
            var nextStmt = new Regex(@"\b(SELECT|INSERT|DELETE|MERGE)\b", RegexOptions.IgnoreCase)
                .Match(rest);
            var len = nextStmt.Success ? nextStmt.Index : rest.Length;
            var body = rest[..len];
            if (Regex.IsMatch(body, @"\bWHERE\b", RegexOptions.IgnoreCase)) continue;

            var line = sql[..m.Index].Count(c => c == '\n') + 1;
            yield return new Diagnostic(Level, Id, Title,
                "UPDATE tanpa WHERE akan mengubah SEMUA baris — risiko massal tanpa rollback.",
                "Tambahkan klausa WHERE; untuk batch production, gunakan log table + batch + PREVIEW.",
                line, m.Index);
        }
    }
}
