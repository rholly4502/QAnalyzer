using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class AnalyzerServiceTests
{
    private readonly AnalyzerService _service = new();

    [Fact]
    public void Analyze_SelectStar_ReturnsWarning()
    {
        var resp = _service.Analyze(new AnalyzeRequest { Sql = "SELECT * FROM Orders;" });

        Assert.NotNull(resp);
        Assert.True(resp.Total >= 1);
        var d = Assert.Single(resp.Diagnostics, x => x.RuleId == "SELECT_STAR");
        Assert.Equal("Warning", d.Level);
    }

    [Fact]
    public void Analyze_ReportingProfile_DowngradesNoLockToInfo()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT * FROM Orders WITH (NOLOCK);",
            Profile = "reporting"
        });

        var nolock = Assert.Single(resp.Diagnostics, x => x.RuleId == "NOLOCK");
        Assert.Equal("Info", nolock.Level);
    }

    [Fact]
    public void Analyze_MaxDopEnabled_AddsOptionAndReportsTransformedSql()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM Orders WHERE Qty > 10;",
            EnableMaxDopTransform = true
        });

        Assert.NotNull(resp.TransformedSql);
        Assert.Contains("MAXDOP 1", resp.TransformedSql);
    }

    [Fact]
    public void Analyze_CriticalSeverity_FlagsHasCritical()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "UPDATE Orders SET Status = 'X';"
        });

        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.Level == "Critical");
    }

    [Fact]
    public void Analyze_MaxDopEnabled_OnCteWithoutSemicolon_AppendsSingleOuterOption()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "WITH cte AS (SELECT Id FROM (SELECT Id FROM Orders) s) SELECT Id FROM cte",
            EnableMaxDopTransform = true
        });

        Assert.NotNull(resp.TransformedSql);
        Assert.EndsWith("OPTION (MAXDOP 1)", resp.TransformedSql);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(resp.TransformedSql, @"OPTION\s*\(\s*MAXDOP\s+1\s*\)"));
    }
}
