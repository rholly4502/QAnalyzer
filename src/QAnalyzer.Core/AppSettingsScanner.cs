using System.Globalization;
using System.Text.Json;

namespace QAnalyzer.Core;

/// <summary>
/// Membaca konfigurasi analyzer dari `appsettings.json` yang berada di folder
/// yang sama dengan script .sql (jika ada). Menyediakan connection string untuk
/// baca DMV + policy analyzer.
/// </summary>
public sealed class AppSettingsScanner
{
    /// <summary>Nama file konfigurasi yang di-scan.</summary>
    public const string FileName = "appsettings.json";

    internal const long MaxFileBytes = 1_048_576;

    /// <summary>Hasil scan: null jika file tidak ada atau tidak valid.</summary>
    public sealed record Result(string ConnectionString, AnalyzerSettings Settings, string SourcePath);

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public Result? ScanForDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var path = Path.Combine(directory, FileName);
        return Scan(path);
    }

    public Result? Scan(string appSettingsPath)
    {
        if (string.IsNullOrWhiteSpace(appSettingsPath)) return null;

        try
        {
            var fileInfo = new FileInfo(appSettingsPath);
            if (!fileInfo.Exists || fileInfo.Length > MaxFileBytes) return null;

            using var stream = File.OpenRead(appSettingsPath);
            using var doc = JsonDocument.Parse(stream, ParseOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var cs = root.TryGetProperty("ConnectionStrings", out var connSection)
                     && connSection.ValueKind == JsonValueKind.Object
                     && connSection.TryGetProperty("Default", out var def)
                     && def.ValueKind == JsonValueKind.String
                ? def.GetString() ?? string.Empty
                : string.Empty;

            var settings = new AnalyzerSettings();
            if (root.TryGetProperty("Analyzer", out var an) && an.ValueKind == JsonValueKind.Object)
            {
                if (an.TryGetProperty("TuningEndpoint", out var ep) && ep.ValueKind == JsonValueKind.String)
                    settings = settings with { TuningEndpoint = ep.GetString() ?? string.Empty };
                if (an.TryGetProperty("TuningApiKey", out var key) && key.ValueKind == JsonValueKind.String)
                    settings = settings with { TuningApiKey = key.GetString() ?? string.Empty };
                if (an.TryGetProperty("EnableMaxDopTransform", out var mx) && mx.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings = settings with { EnableMaxDopTransform = mx.GetBoolean() };
                if (an.TryGetProperty("Profile", out var pf)
                    && pf.ValueKind == JsonValueKind.String
                    && TryParseEnumName<AnalysisProfile>(pf.GetString(), out var parsedProfile))
                    settings = settings with { Profile = parsedProfile };
                if (an.TryGetProperty("RuleSeverityOverrides", out var ov) && ov.ValueKind == JsonValueKind.Object)
                {
                    var map = new Dictionary<string, RiskLevel>(StringComparer.OrdinalIgnoreCase);
                    foreach (var prop in ov.EnumerateObject())
                    {
                        if (!string.IsNullOrWhiteSpace(prop.Name)
                            && prop.Value.ValueKind == JsonValueKind.String
                            && TryParseEnumName<RiskLevel>(prop.Value.GetString(), out var lvl))
                        {
                            map[prop.Name.Trim()] = lvl;
                        }
                    }

                    settings = settings with { RuleSeverityOverrides = map };
                }
            }

            return new Result(cs, settings, appSettingsPath);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // file korup/tidak terbaca: lewati, jangan gagalkan analisis
        }
    }

    private static bool TryParseEnumName<TEnum>(string? raw, out TEnum value) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = default;
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Contains(',', StringComparison.Ordinal)
            || long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            value = default;
            return false;
        }

        return Enum.TryParse(trimmed, ignoreCase: true, out value) && Enum.IsDefined(value);
    }
}
