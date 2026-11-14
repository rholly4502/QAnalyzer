using QAnalyzer.Core;

public sealed class AnalyzerSettingsTests
{
    [Fact]
    public void RuleLevel_NoSettings_ReturnsDefault()
    {
        Assert.Equal(RiskLevel.Warning, RuleLevel.Resolve(RiskLevel.Warning, null, "NOLOCK"));
    }

    [Fact]
    public void RuleLevel_ExplicitOverride_Wins()
    {
        var s = new AnalyzerSettings { RuleSeverityOverrides = new() { ["NOLOCK"] = RiskLevel.Info } };
        Assert.Equal(RiskLevel.Info, RuleLevel.Resolve(RiskLevel.Critical, s, "NOLOCK"));
    }

    [Fact]
    public void RuleLevel_ReportingProfile_DowngradesNoLockAndSelectStar()
    {
        var s = new AnalyzerSettings { Profile = AnalysisProfile.Reporting };
        Assert.Equal(RiskLevel.Info, RuleLevel.Resolve(RiskLevel.Warning, s, "NOLOCK"));
        Assert.Equal(RiskLevel.Info, RuleLevel.Resolve(RiskLevel.Warning, s, "SELECT_STAR"));
        Assert.Equal(RiskLevel.Critical, RuleLevel.Resolve(RiskLevel.Critical, s, "UPDATE_NO_WHERE"));
    }
}
