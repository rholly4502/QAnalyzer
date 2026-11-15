using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Web.Services;

public sealed class AnalyzeRequest
{
    public string Sql { get; set; } = string.Empty;
    public string? Profile { get; set; }          // "general" | "reporting"
    public bool EnableMaxDopTransform { get; set; }
}

public sealed record DiagnosticDto(
    string Level, string RuleId, string Title, string Message,
    string? Suggestion, int Line, int Column);

public sealed class AnalyzeResponse
{
    public int Total { get; init; }
    public bool HasCritical { get; init; }
    public List<DiagnosticDto> Diagnostics { get; init; } = [];
    public string? TransformedSql { get; init; }
}

/// <summary>
/// Tipis: hanya memetakan request -> Core (SqlAnalyzer) + MaxDopTransformer.
/// Core tetap murni; service ini testable tanpa HTTP host.
/// </summary>
public sealed class AnalyzerService
{
    private static readonly ISqlRule[] DefaultRules =
    [
        new SelectStarRule(),
        new NoLockRule(),
        new UpdateWithoutWhereRule(),
    ];

    public AnalyzeResponse Analyze(AnalyzeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = new AnalyzerSettings
        {
            Profile = string.Equals(request.Profile, "reporting", StringComparison.OrdinalIgnoreCase)
                ? AnalysisProfile.Reporting
                : AnalysisProfile.General,
            EnableMaxDopTransform = request.EnableMaxDopTransform,
        };

        var analyzer = new SqlAnalyzer(DefaultRules, settings);
        var report = analyzer.Analyze(request.Sql);

        string? transformed = null;
        if (request.EnableMaxDopTransform)
        {
            transformed = MaxDopTransformer.AddMaxDopOne(request.Sql);
        }

        return new AnalyzeResponse
        {
            Total = report.Total,
            HasCritical = report.HasCritical,
            Diagnostics = report.Diagnostics
                .Select(d => new DiagnosticDto(
                    d.Level.ToString(), d.RuleId, d.Title, d.Message,
                    d.Suggestion, d.Line, d.Column))
                .ToList(),
            TransformedSql = transformed,
        };
    }
}
