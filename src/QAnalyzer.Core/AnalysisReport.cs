namespace QAnalyzer.Core;

/// <summary>Hasil analisis satu script T-SQL.</summary>
public sealed class AnalysisReport
{
    public string Sql { get; init; } = string.Empty;
    public List<Diagnostic> Diagnostics { get; } = [];
    public bool HasCritical => Diagnostics.Any(d => d.Level == RiskLevel.Critical);
    public int Total => Diagnostics.Count;
}
