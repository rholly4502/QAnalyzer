using System.Text.RegularExpressions;

namespace QAnalyzer.Core;

/// <summary>Deteksi SELECT * yang mengambil semua kolom.</summary>
public sealed class SelectStarRule : ISqlRule
{
    public string Id => "SELECTSTAR";
    public string Title => "SELECT *";
    public RiskLevel Level => RiskLevel.Warning;

    private static readonly Regex StarPattern = new(
        @"\bselect\s+\*(?=\s*(?:from|,)|$)",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        foreach (Match m in StarPattern.Matches(sql))
        {
            var (line, col) = GetPosition(sql, m.Index);
            yield return new Diagnostic(
                Level, Id, Title,
                "Query memakai SELECT * yang mengambil semua kolom.",
                "Sebutkan kolom secara eksplisit untuk menghindari kolom tak terduga dan overhead I/O.",
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
