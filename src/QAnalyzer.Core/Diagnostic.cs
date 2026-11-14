namespace QAnalyzer.Core;

/// <summary>F tem analisis untuk satu temuan pada query.</summary>
public sealed record Diagnostic(
    RiskLevel Level,
    string RuleId,
    string Title,
    string Message,
    string? Suggestion = null,
    int Line = 0,
    int Column = 0);
