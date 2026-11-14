using System.Text.RegularExpressions;

namespace QAnalyzer.Core.Rules;

/// <summary>Deteksi `SELECT *` (boros I/O; saran: kolom eksplisit).</summary>
public sealed class SelectStarRule : ISqlRule
{
    public string Id => "SELECT_STAR";
    public string Title => "SELECT * tanpa kolom eksplisit";
    public RiskLevel Level => RiskLevel.Warning;

    private static readonly Regex Re = new(
        @"\bSELECT\s+\*(?![a-zA-Z_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        foreach (Match m in Re.Matches(sql))
        {
            var line = sql[..m.Index].Count(c => c == '\n') + 1;
            yield return new Diagnostic(Level, Id, Title,
                "SELECT * mengambil semua kolom — boros I/O dan rentan bila skema berubah.",
                "Tulis kolom yang dibutuhkan secara eksplisit.",
                line, m.Index);
        }
    }
}
