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
        });

        var report = analyzer.Analyze("UPDATE dbo.T WITH (NOLOCK) SET a=1;");
        // NoLock di update -> Critical (override profile tidak menyentuh UPDATE)
        // Update tanpa WHERE -> Critical
        Assert.Equal(2, report.Total);
        Assert.True(report.HasCritical);
    }
}
