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
        var report = new AnalysisReport { Sql = sql };
        foreach (var rule in _rules)
        {
            foreach (var d in rule.Evaluate(sql))
            {
                var resolved = RuleLevel.Resolve(d.Level, _settings, rule.Id);
                report.Diagnostics.Add(resolved == d.Level ? d : d with { Level = resolved });
            }
        }
        return report;
    }
}
