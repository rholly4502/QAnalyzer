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

    /// <summary>Hasil scan: null jika file tidak ada atau tidak valid.</summary>
    public sealed record Result(string ConnectionString, AnalyzerSettings Settings, string SourcePath);

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public Result? ScanForDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var path = Path.Combine(directory, FileName);
        return Scan(path);
    }

    public Result? Scan(string appSettingsPath)
    {
        if (!File.Exists(appSettingsPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(appSettingsPath));
            var root = doc.RootElement;

            var cs = root.TryGetProperty("ConnectionStrings", out var connSection) &&
                     connSection.TryGetProperty("Default", out var def)
                ? def.GetString() ?? string.Empty
                : string.Empty;

            var settings = new AnalyzerSettings();
            if (root.TryGetProperty("Analyzer", out var an))
            {
                if (an.TryGetProperty("TuningEndpoint", out var ep) && ep.ValueKind == JsonValueKind.String)
                    settings = settings with { TuningEndpoint = ep.GetString() ?? string.Empty };
                if (an.TryGetProperty("TuningApiKey", out var key) && key.ValueKind == JsonValueKind.String)
                    settings = settings with { TuningApiKey = key.GetString() ?? string.Empty };
                if (an.TryGetProperty("EnableMaxDopTransform", out var mx) && mx.ValueKind == JsonValueKind.True)
                    settings = settings with { EnableMaxDopTransform = true };
                if (an.TryGetProperty("Profile", out var pf) &&
                    Enum.TryParse<AnalysisProfile>(pf.GetString(), ignoreCase: true, out var parsed))
                    settings = settings with { Profile = parsed };
                if (an.TryGetProperty("RuleSeverityOverrides", out var ov) && ov.ValueKind == JsonValueKind.Object)
                {
                    var map = new Dictionary<string, RiskLevel>();
                    foreach (var prop in ov.EnumerateObject())
                        if (Enum.TryParse<RiskLevel>(prop.Value.GetString(), ignoreCase: true, out var lvl))
                            map[prop.Name] = lvl;
                    settings = settings with { RuleSeverityOverrides = map };
                }
            }

            return new Result(cs, settings, appSettingsPath);
        }
        catch (JsonException)
        {
            return null; // file korup: lewati, jangan gagalkan analisis
        }
    }
}
