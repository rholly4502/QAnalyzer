using Microsoft.SqlServer.TransactSql.ScriptDom;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core;

/// <summary>
/// Orchestrator analisis statis. Menjalankan semua aturan terhadap satu script
/// T-SQL tanpa eksekusi. Bersih dari dependensi infra/UI sehingga teruji TDD.
/// </summary>
public sealed class SqlAnalyzer
{
    private readonly IReadOnlyList<ISqlRule> _rules;
    private readonly AnalyzerSettings? _settings;

    public static IReadOnlyList<ISqlRule> CreateDefaultRules() =>
    [
        new SqlSyntaxErrorRule(),
        new SelectStarRule(),
        new NoLockRule(),
        new UpdateWithoutWhereRule(),
        new DeleteWithoutWhereRule(),
        new TruncateTableRule(),
        new NonSargablePredicateRule(),
        new DynamicSqlRule(),
        new CursorUsageRule(),
    ];

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
        IReadOnlyList<ParseError> parseErrors = [];
        var parsed = false;

        foreach (var rule in _rules)
        {
            IEnumerable<Diagnostic> diagnostics;
            if (rule is IAstSqlRule or IAstParseErrorRule)
            {
                if (!parsed)
                {
                    parsedFragment = SqlAstParser.Parse(sql, out var errors);
                    parseErrors = errors as IReadOnlyList<ParseError> ?? errors.ToList();
                    parsed = true;
                }

                diagnostics = rule switch
                {
                    IAstParseErrorRule errorRule => errorRule.Evaluate(parseErrors),
                    IAstSqlRule astRule => parsedFragment is null ? [] : astRule.Evaluate(parsedFragment),
                    _ => [],
                };
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
