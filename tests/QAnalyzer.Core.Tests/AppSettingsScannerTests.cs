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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
