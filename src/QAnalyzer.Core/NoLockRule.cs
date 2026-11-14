using System.Text.RegularExpressions;

namespace QAnalyzer.Core;

/// <summary>Deteksi hint NOLOCK yang membaca data uncommitted.</summary>
public sealed class NoLockRule : ISqlRule
{
    public string Id => "NOLOCK";
    public string Title => "NOLOCK";
    public RiskLevel Level => RiskLevel.Warning;

    private static readonly Regex NoLockPattern = new(
        @"\bNOLOCK\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        foreach (Match m in NoLockPattern.Matches(sql))
        {
            var (line, col) = GetPosition(sql, m.Index);
            yield return new Diagnostic(
                Level, Id, Title,
                "Query memakai hint NOLOCK (READ UNCOMMITTED) yang bisa membaca data dirty/uncommitted.",
                "Hindari NOLOCK; gunakan snapshot isolation atau indexing yang tepat untuk konsistensi baca.",
                line, col);
        }
    }

    private static (int Line, int Column) GetPosition(string sql, int index)
    {
        var line = 1;
        var col = 1;
        for (var i = 0; i < index && i < sql.Length; i++)
        {
            if (sql[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }
}
