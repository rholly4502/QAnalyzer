using System.Text;

namespace QAnalyzer.Core;

/// <summary>Memformat satu diagnostic menjadi blok teks detail (untuk UI & web).</summary>
public static class DiagnosticDetailFormatter
{
    public static string Format(Diagnostic d)
    {
        var sb = new StringBuilder();
        sb.Append('[').Append(d.Level).Append("] ");
        sb.Append(d.Title).Append(" (").Append(d.RuleId).Append(')').AppendLine();
        sb.Append("Baris ").Append(d.Line);
        if (d.Column > 0)
            sb.Append(", Kolom ").Append(d.Column);
        sb.AppendLine();
        sb.AppendLine(d.Message);
        if (!string.IsNullOrWhiteSpace(d.Suggestion))
            sb.Append("Saran: ").Append(d.Suggestion).AppendLine();
        return sb.ToString();
    }
}
