using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

public sealed class SqlRulesTests
{
    [Fact]
    public void SelectStarRule_FlagsSelectStar()
    {
        var rule = new SelectStarRule();
        var diags = rule.Evaluate("SELECT * FROM dbo.Customers;").ToList();
        Assert.Single(diags);
        Assert.Equal("SELECT_STAR", diags[0].RuleId);
        Assert.Equal(RiskLevel.Warning, diags[0].Level);
    }

    [Fact]
    public void NoLockRule_PlainSelect_IsInfo()
    {
        var rule = new NoLockRule();
        var diags = rule.Evaluate("SELECT a FROM dbo.T WITH (NOLOCK);").ToList();
        Assert.Single(diags);
        Assert.Equal(RiskLevel.Info, diags[0].Level);
    }

    [Fact]
    public void NoLockRule_WithUpdate_IsCritical()
    {
        var rule = new NoLockRule();
        var diags = rule.Evaluate("UPDATE dbo.T WITH (NOLOCK) SET a=1;").ToList();
        Assert.Single(diags);
        Assert.Equal(RiskLevel.Critical, diags[0].Level);
    }

    [Fact]
    public void NoLockRule_WithSelectStar_IsWarning()
    {
        var rule = new NoLockRule();
        var diags = rule.Evaluate("SELECT * FROM dbo.T WITH (NOLOCK);").ToList();
        Assert.Single(diags);
        Assert.Equal(RiskLevel.Warning, diags[0].Level);
    }

    [Fact]
    public void UpdateWithoutWhereRule_FlagsBareUpdate()
    {
        var rule = new UpdateWithoutWhereRule();
        var diags = rule.Evaluate("UPDATE dbo.Customers SET active=0;").ToList();
        Assert.Single(diags);
        Assert.Equal(RiskLevel.Critical, diags[0].Level);
    }

    [Fact]
    public void UpdateWithoutWhereRule_SkipsWhenWherePresent()
    {
        var rule = new UpdateWithoutWhereRule();
        var diags = rule.Evaluate("UPDATE dbo.Customers SET active=0 WHERE id=@id;").ToList();
        Assert.Empty(diags);
    }

    [Fact]
    public void Analyzer_RunsAllDefaultRules_WithSettingsOverride()
    {
        var settings = new AnalyzerSettings { Profile = AnalysisProfile.Reporting };
        var analyzer = new SqlAnalyzer(new ISqlRule[]
        {
            new SelectStarRule(), new NoLockRule(), new UpdateWithoutWhereRule(),
        }, settings);

        var report = analyzer.Analyze("UPDATE dbo.T WITH (NOLOCK) SET a=1;");
        // NoLock di update -> Critical (override profile tidak menyentuh UPDATE)
        // Update tanpa WHERE -> Critical
        Assert.Equal(2, report.Total);
        Assert.True(report.HasCritical);
        Assert.All(report.Diagnostics, d => Assert.Equal(RiskLevel.Critical, d.Level));
    }

    [Fact]
    public void Analyzer_ReportingProfile_PreservesCriticalNoLockOnWriteStatementsWithWhere()
    {
        var settings = new AnalyzerSettings { Profile = AnalysisProfile.Reporting };
        var analyzer = new SqlAnalyzer(SqlAnalyzer.CreateDefaultRules(), settings);

        var writeQueries = new[]
        {
            "UPDATE dbo.T WITH (NOLOCK) SET a = 1 WHERE Id = 1;",
            "DELETE FROM dbo.T WITH (NOLOCK) WHERE Id = 1;",
            "INSERT INTO dbo.Audit (Id) SELECT Id FROM dbo.T WITH (NOLOCK);",
            "MERGE dbo.Target AS t USING dbo.Source AS s WITH (NOLOCK) ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Val = s.Val;",
        };

        foreach (var sql in writeQueries)
        {
            var report = analyzer.Analyze(sql);
            Assert.True(report.HasCritical, $"Expected Critical diagnostic for: {sql}");
            var nolock = Assert.Single(report.Diagnostics, d => d.RuleId == "NOLOCK");
            Assert.Equal(RiskLevel.Critical, nolock.Level);
        }

        var readReport = analyzer.Analyze("SELECT * FROM dbo.T WITH (NOLOCK);");
        Assert.False(readReport.HasCritical);
        var selectStar = Assert.Single(readReport.Diagnostics, d => d.RuleId == "SELECT_STAR");
        var readNoLock = Assert.Single(readReport.Diagnostics, d => d.RuleId == "NOLOCK");
        Assert.Equal(RiskLevel.Info, selectStar.Level);
        Assert.Equal(RiskLevel.Info, readNoLock.Level);
    }
}
