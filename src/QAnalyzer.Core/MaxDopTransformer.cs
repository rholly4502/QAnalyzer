using System.Text.RegularExpressions;

namespace QAnalyzer.Core;

/// <summary>
/// Menghasilkan versi "hemat resource" query: menambahkan `OPTION (MAXDOP 1)`
/// di akhir tiap SELECT terluar. Aman: tidak menyentuh query yang sudah punya
/// klausa OPTION; hanya satu OPTION yang diperbolehkan dan harus di paling akhir.
/// </summary>
public static class MaxDopTransformer
{
    public static string AddMaxDopOne(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return sql;

        // Bagi menjadi statement berdasar titik koma (pendekatan sederhana).
        // Skip jika ada OPTION existing di statement yang sama.
        var statementPattern = new Regex(
            @"(?<before>.*?)(?<stmt>\bSELECT\b.*?)(?<end>;|(?=\s*\bSELECT\b))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        var result = statementPattern.Replace(sql, m =>
        {
            var stmt = m.Groups["stmt"].Value;
            if (Regex.IsMatch(stmt, @"\bOPTION\s*\(", RegexOptions.IgnoreCase))
                return m.Value; // sudah ada OPTION — jangan sentuh
            var trimmed = stmt.TrimEnd();
            var injection = trimmed.EndsWith(")", StringComparison.Ordinal) ||
                            trimmed.EndsWith("]", StringComparison.Ordinal) ||
                            trimmed.EndsWith("'", StringComparison.Ordinal)
                ? " OPTION (MAXDOP 1)" : " OPTION (MAXDOP 1)";
            return m.Groups["before"].Value + stmt + injection + m.Groups["end"].Value;
        });

        return result;
    }
}
