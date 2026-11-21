namespace QAnalyzer.Core;

/// <summary>Hasil gabungan analisis statis T-SQL dan XML execution plan lokal.</summary>
public sealed record LocalPlanAnalysisResult(
    IReadOnlyList<Diagnostic> Diagnostics,
    ExecutionPlanSource? PlanSource,
    int PlanDiagnosticCount,
    string? PlanNote);

/// <summary>
/// Menggabungkan analisis aturan statis T-SQL dengan pengambilan serta parsing XML execution plan
/// lokal dari <see cref="ISqlMetadataReader"/> tanpa mengeksekusi mutasi data.
/// </summary>
public sealed class LocalExecutionPlanAnalyzer(
    IEnumerable<ISqlRule> rules,
    AnalyzerSettings? settings = null,
    ExecutionPlanParser? planParser = null)
{
    private readonly SqlAnalyzer _sqlAnalyzer = new(rules ?? throw new ArgumentNullException(nameof(rules)), settings);
    private readonly AnalyzerSettings? _settings = settings;
    private readonly ExecutionPlanParser _planParser = planParser ?? new ExecutionPlanParser();

    public async Task<LocalPlanAnalysisResult> AnalyzeAsync(
        string sql,
        ISqlMetadataReader metadataReader,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(metadataReader);

        var combined = new List<Diagnostic>(_sqlAnalyzer.Analyze(sql).Diagnostics);
        var planResult = await metadataReader.GetExecutionPlanXmlAsync(sql, ct);
        if (planResult is null || string.IsNullOrWhiteSpace(planResult.PlanXml))
        {
            var note = ExecutionPlanQueryGuard.CanSafelyEstimatePlan(sql, out var reason)
                ? "Execution plan belum tersedia di plan cache maupun estimated plan server."
                : $"Plan cache kosong; estimated plan dilewati untuk keamanan ({reason})";
            return new LocalPlanAnalysisResult(combined, null, 0, note);
        }

        var planDiagnostics = _planParser.Parse(planResult.PlanXml);
        foreach (var d in planDiagnostics)
        {
            var resolved = RuleLevel.Resolve(d.Level, _settings, d.RuleId);
            combined.Add(resolved == d.Level ? d : d with { Level = resolved });
        }

        return new LocalPlanAnalysisResult(combined, planResult.Source, planDiagnostics.Count, null);
    }
}
