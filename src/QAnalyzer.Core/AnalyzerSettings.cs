using System.Text.Json.Serialization;

namespace QAnalyzer.Core;

/// <summary>Policy pengguna: override severity per rule (mis. mode "reporting").</summary>
public sealed record AnalyzerSettings
{
    /// <summary>Peta rule-id -> severity override (kosong = pakai default rule).</summary>
    public Dictionary<string, RiskLevel> RuleSeverityOverrides { get; init; } = new();

    /// <summary>Aktifkan transform "hemat resource" (OPTION MAXDOP 1).</summary>
    public bool EnableMaxDopTransform { get; init; } = false;

    /// <summary>Endpoint gateway Hermes (OpenAI-compatible), mis. http://host:5000/v1.</summary>
    public string TuningEndpoint { get; init; } = string.Empty;

    /// <summary>API key gateway (untuk tombol Tuning). Jangan di-commit.</summary>
    public string TuningApiKey { get; init; } = string.Empty;

    /// <summary>Mode konteks: "general" | "reporting" (mengecilkan NOLOCK/SELECT * ke Info).</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AnalysisProfile Profile { get; init; } = AnalysisProfile.General;
}

public enum AnalysisProfile { General, Reporting }

/// <summary>Menerapkan override settings terhadap level default sebuah rule.</summary>
public static class RuleLevel
{
    public static RiskLevel Resolve(RiskLevel defaultLevel, AnalyzerSettings? settings, string ruleId)
    {
        if (settings is null) return defaultLevel;
        if (settings.RuleSeverityOverrides.TryGetValue(ruleId, out var overridden)) return overridden;
        if (settings.Profile == AnalysisProfile.Reporting &&
            (ruleId == "NOLOCK" || ruleId == "SELECT_STAR")) return RiskLevel.Info;
        return defaultLevel;
    }
}
