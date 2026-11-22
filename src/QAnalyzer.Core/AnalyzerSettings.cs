using System.Text.Json.Serialization;

namespace QAnalyzer.Core;

/// <summary>Policy pengguna: override severity per rule (mis. mode "reporting").</summary>
public sealed record AnalyzerSettings
{
    /// <summary>Peta rule-id -> severity override (kosong = pakai default rule).</summary>
    public Dictionary<string, RiskLevel> RuleSeverityOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);

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
        if (settings is null || string.IsNullOrWhiteSpace(ruleId))
        {
            return defaultLevel;
        }

        if (TryGetOverride(settings.RuleSeverityOverrides, ruleId, out var overridden))
        {
            return overridden;
        }

        // Di profile Reporting hanya turunkan temuan read/non-critical; temuan Critical (mis. NOLOCK pada statement tulis) tetap Critical.
        if (settings.Profile == AnalysisProfile.Reporting
            && defaultLevel != RiskLevel.Critical
            && (string.Equals(ruleId, "NOLOCK", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ruleId, "SELECT_STAR", StringComparison.OrdinalIgnoreCase)))
        {
            return RiskLevel.Info;
        }

        return defaultLevel;
    }

    private static bool TryGetOverride(
        IReadOnlyDictionary<string, RiskLevel>? overrides,
        string ruleId,
        out RiskLevel overridden)
    {
        if (overrides is null || overrides.Count == 0)
        {
            overridden = default;
            return false;
        }

        if (overrides.TryGetValue(ruleId, out overridden))
        {
            return Enum.IsDefined(overridden);
        }

        foreach (var kv in overrides)
        {
            if (string.Equals(kv.Key, ruleId, StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.IsDefined(kv.Value))
                {
                    overridden = kv.Value;
                    return true;
                }

                return false;
            }
        }

        return false;
    }
}
