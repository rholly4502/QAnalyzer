using System.Text;

namespace QAnalyzer.Core;

/// <summary>Memformat daftar diagnostic menjadi laporan Markdown / CSV (murni, tanpa UI).
/// Dipakai WPF (export/salin) dan nanti web demo.</summary>
public static class ReportFormatter
{
    public static string ToMarkdown(IEnumerable<Diagnostic> diagnostics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# QAnalyzer Report");
        foreach (var g in diagnostics.GroupBy(d => d.Level)
                     .OrderByDescending(g => g.Key))
        {
            sb.AppendLine().Append("## ").Append(g.Key).AppendLine();
            foreach (var d in g)
            {
                sb.Append("- **[Line ").Append(d.Line).Append("]** ")
                  .Append(d.RuleId).Append(": ").Append(d.Message);
                if (!string.IsNullOrWhiteSpace(d.Suggestion))
                    sb.Append(" → *").Append(d.Suggestion).Append('*');
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    public static string ToCsv(IEnumerable<Diagnostic> diagnostics)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Level,RuleId,Line,Column,Message,Suggestion");
        foreach (var d in diagnostics)
        {
            sb.Append(CsvField(d.Level.ToString())).Append(',')
              .Append(CsvField(d.RuleId)).Append(',')
              .Append(CsvField(d.Line.ToString())).Append(',')
              .Append(CsvField(d.Column.ToString())).Append(',')
              .Append(CsvField(d.Message)).Append(',')
              .Append(CsvField(d.Suggestion ?? string.Empty)).AppendLine();
        }
        return sb.ToString();
    }

    private static string CsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}
