namespace QAnalyzer.Core;

/// <summary>Ringkasan jumlah diagnostic per level (data dashboard).</summary>
public sealed record DiagnosticCounts(int Info, int Warning, int Critical)
{
    public int Total => Info + Warning + Critical;
}

/// <summary>Filter tampilan: hanya tampilkan level yang dicentang.</summary>
public sealed record LevelFilter
{
    public bool Info { get; init; } = true;
    public bool Warning { get; init; } = true;
    public bool Critical { get; init; } = true;

    public bool IsVisible(RiskLevel level) => level switch
    {
        RiskLevel.Info => Info,
        RiskLevel.Warning => Warning,
        RiskLevel.Critical => Critical,
        _ => false,
    };
}

/// <summary>Utilitas murni untuk dashboard dan filter hasil.</summary>
public static class DiagnosticFilter
{
    public static DiagnosticCounts CountByLevel(IEnumerable<Diagnostic> diagnostics)
    {
        var arr = diagnostics.ToArray();
        return new DiagnosticCounts(
            arr.Count(d => d.Level == RiskLevel.Info),
            arr.Count(d => d.Level == RiskLevel.Warning),
            arr.Count(d => d.Level == RiskLevel.Critical));
    }

    public static IEnumerable<Diagnostic> Apply(IEnumerable<Diagnostic> diagnostics, LevelFilter filter)
        => diagnostics.Where(d => filter.IsVisible(d.Level));
}
