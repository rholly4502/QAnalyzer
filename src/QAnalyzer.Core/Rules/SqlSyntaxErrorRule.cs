using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core.Rules;

/// <summary>
/// Deteksi kesalahan sintaks T-SQL via parser AST ScriptDom agar query yang malformed
/// tidak lolos dengan hasil analisis kosong palsu.
/// </summary>
public sealed class SqlSyntaxErrorRule : IAstParseErrorRule
{
    public const int MaxReportedErrors = 20;
    private const int MaxErrorMessageLength = 240;

    private const string DefaultSuggestion =
        "Perbaiki sintaks T-SQL pada baris dan kolom yang ditunjukkan, lalu jalankan ulang analisis agar seluruh aturan statis dapat mengevaluasi query.";

    public string Id => "SQL_SYNTAX_ERROR";
    public string Title => "Kesalahan sintaks T-SQL";
    public RiskLevel Level => RiskLevel.Critical;

    public IEnumerable<Diagnostic> Evaluate(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        SqlAstParser.Parse(sql, out var errors);
        return Evaluate(errors as IReadOnlyList<ParseError> ?? errors.ToList());
    }

    IEnumerable<Diagnostic> IAstParseErrorRule.Evaluate(IReadOnlyList<ParseError> errors) => Evaluate(errors);

    private IEnumerable<Diagnostic> Evaluate(IReadOnlyList<ParseError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<(int Line, int Column, string Detail)>();
        var diagnostics = new List<Diagnostic>(Math.Min(errors.Count, MaxReportedErrors));

        foreach (var error in errors)
        {
            if (error is null)
            {
                continue;
            }

            var line = Math.Max(0, error.Line);
            var column = Math.Max(0, error.Column);
            var detail = NormalizeDetail(error.Message);

            if (!seen.Add((line, column, detail)))
            {
                continue;
            }

            var codePart = error.Number > 0 ? $" (kode {error.Number})" : string.Empty;
            var message = $"Syntax error T-SQL{codePart}: {detail} Analisis statis pada bagian yang bermasalah mungkin tidak lengkap.";

            diagnostics.Add(new Diagnostic(
                Level,
                Id,
                Title,
                message,
                DefaultSuggestion,
                line,
                column));

            if (diagnostics.Count >= MaxReportedErrors)
            {
                break;
            }
        }

        return diagnostics;
    }

    private static string NormalizeDetail(string? rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return "Kesalahan sintaks tidak diketahui.";
        }

        var sb = new StringBuilder(rawMessage.Length);
        var pendingSpace = false;
        foreach (var ch in rawMessage)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                if (sb.Length > 0)
                {
                    pendingSpace = true;
                }

                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(ch);
        }

        if (sb.Length == 0)
        {
            return "Kesalahan sintaks tidak diketahui.";
        }

        var normalized = sb.ToString();
        return normalized.Length <= MaxErrorMessageLength
            ? normalized
            : normalized[..MaxErrorMessageLength] + "…";
    }
}
