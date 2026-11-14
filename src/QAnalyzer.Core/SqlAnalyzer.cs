namespace QAnalyzer.Core;

/// <summary>
/// Orchestrator analisis statis. Menjalankan semua aturan terhadap satu script
/// T-SQL tanpa eksekusi. Bersih dari dependensi infra/UI sehingga teruji TDD.
/// </summary>
public sealed class SqlAnalyzer
{
    private readonly IReadOnlyList<ISqlRule> _rules;

    public SqlAnalyzer(IEnumerable<ISqlRule> rules) => _rules = rules.ToList();

    public AnalysisReport Analyze(string sql)
    {
        var report = new AnalysisReport { Sql = sql };
        foreach (var rule in _rules)
            foreach (var d in rule.Evaluate(sql))
                report.Diagnostics.Add(d);
        return report;
    }
}
