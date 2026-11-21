using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace QAnalyzer.Core;

/// <summary>
/// Orchestrator analisis statis. Menjalankan semua aturan terhadap satu script
/// T-SQL tanpa eksekusi. Bersih dari dependensi infra/UI sehingga teruji TDD.
/// </summary>
public sealed class SqlAnalyzer
{
    private readonly IReadOnlyList<ISqlRule> _rules;
    private readonly AnalyzerSettings? _settings;

    public SqlAnalyzer(IEnumerable<ISqlRule> rules, AnalyzerSettings? settings = null)
    {
        _rules = rules.ToList();
        _settings = settings;
    }

    public AnalysisReport Analyze(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var report = new AnalysisReport { Sql = sql };

        TSqlFragment? parsedFragment = null;
        var parsed = false;

        foreach (var rule in _rules)
        {
            IEnumerable<Diagnostic> diagnostics;
            if (rule is IAstSqlRule astRule)
            {
                if (!parsed)
                {
                    parsedFragment = SqlAstParser.Parse(sql);
                    parsed = true;
                }

                diagnostics = parsedFragment is null ? [] : astRule.Evaluate(parsedFragment);
            }
            else
            {
                diagnostics = rule.Evaluate(sql);
            }

            foreach (var d in diagnostics)
            {
                var resolved = RuleLevel.Resolve(d.Level, _settings, rule.Id);
                report.Diagnostics.Add(resolved == d.Level ? d : d with { Level = resolved });
            }
        }
        return report;
    }
}
