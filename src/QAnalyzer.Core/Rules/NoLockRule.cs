using System.Text.RegularExpressions;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi `WITH (NOLOCK)` / `NOLOCK`. Di SELECT polos normal (Info); naik
/// Warning bila gabung SELECT *; naik Critical bila di UPDATE/DELETE/INSERT.
/// </summary>
public sealed class NoLockRule : ISqlRule
{
    public string Id => "NOLOCK";
    public string Title => "Hint NOLOCK digunakan";
    public RiskLevel Level => RiskLevel.Info;

    private static readonly Regex LockRe = new(
        @"\b(?:WITH\s*\(\s*)?NOLOCK\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WriteRe = new(
        @"\b(UPDATE|DELETE|INSERT|MERGE)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SelectStarRe = new(
        @"\bSELECT\s+\*(?![a-zA-Z_])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        foreach (Match m in LockRe.Matches(sql))
        {
            var preceding = sql[..m.Index];
            var line = preceding.Count(c => c == '\n') + 1;
            var level = Level;
            var message = "Hint NOLOCK dipakai pada read query — normal untuk reporting/read-only.";
            var suggestion = "Pastikan query/index sudah di-tuning; NOLOCK bukan pengganti tuning.";

            if (WriteRe.IsMatch(preceding))
            {
                level = RiskLevel.Critical;
                message = "NOLOCK pada statement tulis (UPDATE/DELETE/INSERT) berbahaya — read-uncommitted pada data yang berubah.";
                suggestion = "Hapus NOLOCK pada statement tulis; gunakan isolasi yang tepat.";
            }
            else if (SelectStarRe.IsMatch(preceding[^Math.Min(preceding.Length, 400)..]))
            {
                level = RiskLevel.Warning;
                message = "NOLOCK digabung SELECT * — boros I/O + risiko dirty read.";
                suggestion = "Pilih kolom eksplisit dan hindari NOLOCK tanpa alasan.";
            }

            yield return new Diagnostic(level, Id, Title, message, suggestion, line, m.Index);
        }
    }
}
