using QAnalyzer.Core;

public sealed class AppSettingsScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "qa_scanner_" + Guid.NewGuid().ToString("N"));
    private readonly AppSettingsScanner _scanner = new();

    public AppSettingsScannerTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public void Scan_WhenNoFile_ReturnsNull()
    {
        Assert.Null(_scanner.ScanForDirectory(_dir));
    }

    [Fact]
    public void Scan_ReadsConnectionStringAndProfile()
    {
        File.WriteAllText(Path.Combine(_dir, AppSettingsScanner.FileName),
            """
{
  "ConnectionStrings": { "Default": "Server=localhost;Database=AdventureWorks;Integrated Security=true" },
  "Analyzer": { "Profile": "Reporting", "EnableMaxDopTransform": true }
}
""");
        var r = _scanner.ScanForDirectory(_dir);
        Assert.NotNull(r);
        Assert.Contains("localhost", r!.ConnectionString);
        Assert.Equal(AnalysisProfile.Reporting, r.Settings.Profile);
        Assert.True(r.Settings.EnableMaxDopTransform);
    }

    [Fact]
    public void Scan_WhenInvalidJson_ReturnsNullWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_dir, AppSettingsScanner.FileName), "{ not valid json");
        Assert.Null(_scanner.ScanForDirectory(_dir));
    }

    [Fact]
    public void Scan_WithNonObjectRootOrMismatchedPropertyTypes_DoesNotThrow()
    {
        foreach (var rootJson in new[] { "null", "[]", "\"text\"", "123" })
        {
            File.WriteAllText(Path.Combine(_dir, AppSettingsScanner.FileName), rootJson);
            Assert.Null(_scanner.ScanForDirectory(_dir));
        }

        File.WriteAllText(Path.Combine(_dir, AppSettingsScanner.FileName),
            """
            {
              "ConnectionStrings": null,
              "Analyzer": {
                "TuningEndpoint": 123,
                "TuningApiKey": true,
                "EnableMaxDopTransform": "true",
                "Profile": 123,
                "RuleSeverityOverrides": {
                  "NOLOCK": 123,
                  "SELECT_STAR": null,
                  "UPDATE_NO_WHERE": "999",
                  "  ": "Warning"
                }
              }
            }
            """);
        var result = _scanner.ScanForDirectory(_dir);
        Assert.NotNull(result);
        Assert.Equal(string.Empty, result!.ConnectionString);
        Assert.Equal(AnalysisProfile.General, result.Settings.Profile);
        Assert.False(result.Settings.EnableMaxDopTransform);
        Assert.Equal(string.Empty, result.Settings.TuningEndpoint);
        Assert.Equal(string.Empty, result.Settings.TuningApiKey);
        Assert.Empty(result.Settings.RuleSeverityOverrides);

        File.WriteAllText(Path.Combine(_dir, AppSettingsScanner.FileName),
            """
            {
              "ConnectionStrings": { "Default": 42 },
              "Analyzer": []
            }
            """);
        var arraySectionResult = _scanner.ScanForDirectory(_dir);
        Assert.NotNull(arraySectionResult);
        Assert.Equal(string.Empty, arraySectionResult!.ConnectionString);
        Assert.Equal(AnalysisProfile.General, arraySectionResult.Settings.Profile);
        Assert.False(arraySectionResult.Settings.EnableMaxDopTransform);
    }

    [Fact]
    public void Scan_RejectsNumericEnumStringsAndReadsCaseInsensitiveOverridesAndComments()
    {
        File.WriteAllText(Path.Combine(_dir, AppSettingsScanner.FileName),
            """
            {
              // komentar konfigurasi lokal
              "Analyzer": {
                "Profile": "999",
                "EnableMaxDopTransform": false,
                "RuleSeverityOverrides": {
                  "nolock": "Warning",
                  "SELECT_STAR": "0",
                  "DELETE_NO_WHERE": "999",
                },
              },
            }
            """);

        var r1 = _scanner.ScanForDirectory(_dir);
        Assert.NotNull(r1);
        Assert.Equal(AnalysisProfile.General, r1!.Settings.Profile);
        Assert.False(r1.Settings.EnableMaxDopTransform);
        Assert.Single(r1.Settings.RuleSeverityOverrides);
        Assert.True(r1.Settings.RuleSeverityOverrides.TryGetValue("NOLOCK", out var level));
        Assert.Equal(RiskLevel.Warning, level);

        File.WriteAllText(Path.Combine(_dir, AppSettingsScanner.FileName),
            """
            {
              "Analyzer": {
                "Profile": "Reporting",
                "EnableMaxDopTransform": false
              }
            }
            """);

        var r2 = _scanner.ScanForDirectory(_dir);
        Assert.NotNull(r2);
        Assert.Equal(AnalysisProfile.Reporting, r2!.Settings.Profile);
        Assert.False(r2.Settings.EnableMaxDopTransform);
    }

    [Fact]
    public void Scan_NullWhitespaceOrOversizedFile_ReturnsNull()
    {
        Assert.Null(_scanner.Scan(string.Empty));
        Assert.Null(_scanner.Scan("   "));
        Assert.Null(_scanner.Scan(null!));

        var exactLimitPath = Path.Combine(_dir, "exact.json");
        File.WriteAllText(exactLimitPath, "{}" + new string(' ', 1_048_576 - 2));
        Assert.NotNull(_scanner.Scan(exactLimitPath));

        var overLimitPath = Path.Combine(_dir, "oversized.json");
        File.WriteAllText(overLimitPath, "{}" + new string(' ', 1_048_576 - 1));
        Assert.Null(_scanner.Scan(overLimitPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
