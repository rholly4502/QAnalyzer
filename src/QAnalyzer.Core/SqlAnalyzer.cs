using Microsoft.SqlServer.TransactSql.ScriptDom;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core;

/// <summary>
/// Orchestrator analisis statis. Menjalankan semua aturan terhadap satu script
/// T-SQL tanpa eksekusi. Bersih dari dependensi infra/UI sehingga teruji TDD.
/// </summary>
public sealed class SqlAnalyzer
{
    public const int MaxNestingDepth = 128;
    public const int MaxConsecutiveUnaryOperators = 64;
    public const int MaxChainedOperatorsPerStatement = 1024;
    public const int MaxDiagnostics = 200;

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
        new NotInSubqueryRule(),
        new NullComparisonRule(),
        new UnspecifiedTypeLengthRule(),
        new TopWithoutOrderByRule(),
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
        var hasAstRules = _rules.Any(static r => r is IAstSqlRule or IAstParseErrorRule);

        var criticals = new List<(int Sequence, Diagnostic Diagnostic)>();
        var warnings = new List<(int Sequence, Diagnostic Diagnostic)>();
        var infos = new List<(int Sequence, Diagnostic Diagnostic)>();
        var sequence = 0;

        foreach (var rule in _rules)
        {
            if (criticals.Count >= MaxDiagnostics)
            {
                if (!hasAstRules || parsed) break;
                if (rule is not (IAstSqlRule or IAstParseErrorRule)) continue;

                parsedFragment = SqlAstParser.Parse(sql, out var errors);
                parseErrors = errors as IReadOnlyList<ParseError> ?? errors.ToList();
                parsed = true;
                break;
            }

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
                var item = resolved == d.Level ? d : d with { Level = resolved };
                var seq = sequence++;

                switch (item.Level)
                {
                    case RiskLevel.Critical when criticals.Count < MaxDiagnostics:
                        criticals.Add((seq, item));
                        break;
                    case RiskLevel.Warning when criticals.Count + warnings.Count < MaxDiagnostics:
                        warnings.Add((seq, item));
                        break;
                    case RiskLevel.Info when criticals.Count + warnings.Count + infos.Count < MaxDiagnostics:
                        infos.Add((seq, item));
                        break;
                }

                if (criticals.Count >= MaxDiagnostics && (!hasAstRules || parsed)) break;
            }
        }

        var kept = new List<(int Sequence, Diagnostic Diagnostic)>(
            Math.Min(MaxDiagnostics, criticals.Count + warnings.Count + infos.Count));
        kept.AddRange(criticals);
        AddWithinBudget(kept, warnings);
        AddWithinBudget(kept, infos);
        kept.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));

        foreach (var (_, diagnostic) in kept)
        {
            report.Diagnostics.Add(diagnostic);
        }

        return report;
    }

    private static void AddWithinBudget(
        List<(int Sequence, Diagnostic Diagnostic)> target,
        List<(int Sequence, Diagnostic Diagnostic)> source)
    {
        var remaining = MaxDiagnostics - target.Count;
        if (remaining > 0 && source.Count > 0)
        {
            target.AddRange(source.Count <= remaining ? source : source.Take(remaining));
        }
    }
}
