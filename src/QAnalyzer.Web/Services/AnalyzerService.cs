using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Web.Services;

public sealed class AnalyzeRequest
{
    public string Sql { get; set; } = string.Empty;
    public string? Profile { get; set; }          // "general" | "reporting"
    public bool EnableMaxDopTransform { get; set; }
    public string? SqlPlanXml { get; set; }
}

public sealed record DiagnosticDto(
    string Level, string RuleId, string Title, string Message,
    string? Suggestion, int Line, int Column);

public sealed class AnalyzeResponse
{
    public int Total { get; init; }
    public bool HasCritical { get; init; }
    public bool HasExecutionPlan { get; init; }
    public int PlanDiagnosticCount { get; init; }
    public List<DiagnosticDto> Diagnostics { get; init; } = [];
    public string? TransformedSql { get; init; }
}

/// <summary>
/// Tipis: memetakan request -> Core (SqlAnalyzer + ExecutionPlanParser) + MaxDopTransformer.
/// Core tetap murni; service ini testable tanpa HTTP host.
/// </summary>
public sealed class AnalyzerService
{
    private static readonly ISqlRule[] DefaultRules =
    [
        new SqlSyntaxErrorRule(),
        new SelectStarRule(),
        new NoLockRule(),
        new UpdateWithoutWhereRule(),
        new DeleteWithoutWhereRule(),
        new TruncateTableRule(),
        new NonSargablePredicateRule(),
    ];

    private readonly ExecutionPlanParser _planParser = new();

    public AnalyzeResponse Analyze(AnalyzeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sql = request.Sql ?? string.Empty;
        var hasSql = !string.IsNullOrWhiteSpace(sql);
        var hasExecutionPlan = !string.IsNullOrWhiteSpace(request.SqlPlanXml);

        var settings = new AnalyzerSettings
        {
            Profile = string.Equals(request.Profile, "reporting", StringComparison.OrdinalIgnoreCase)
                ? AnalysisProfile.Reporting
                : AnalysisProfile.General,
            EnableMaxDopTransform = request.EnableMaxDopTransform,
        };

        var combined = new List<Diagnostic>();
        if (hasSql)
        {
            var analyzer = new SqlAnalyzer(DefaultRules, settings);
            combined.AddRange(analyzer.Analyze(sql).Diagnostics);
        }

        var planDiagnosticCount = 0;
        if (hasExecutionPlan)
        {
            var planDiagnostics = _planParser.Parse(request.SqlPlanXml!);
            planDiagnosticCount = planDiagnostics.Count;
            foreach (var d in planDiagnostics)
            {
                var resolved = RuleLevel.Resolve(d.Level, settings, d.RuleId);
                combined.Add(resolved == d.Level ? d : d with { Level = resolved });
            }
        }

        string? transformed = null;
        if (request.EnableMaxDopTransform && hasSql)
        {
            transformed = MaxDopTransformer.AddMaxDopOne(sql);
        }

        return new AnalyzeResponse
        {
            Total = combined.Count,
            HasCritical = combined.Any(d => d.Level == RiskLevel.Critical),
            HasExecutionPlan = hasExecutionPlan,
            PlanDiagnosticCount = planDiagnosticCount,
            Diagnostics = combined
                .Select(d => new DiagnosticDto(
                    d.Level.ToString(), d.RuleId, d.Title, d.Message,
                    d.Suggestion, d.Line, d.Column))
                .ToList(),
            TransformedSql = transformed,
        };
    }
}
