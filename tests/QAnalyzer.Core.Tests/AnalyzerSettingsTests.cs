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

    [Fact]
    public void RuleLevel_ReportingProfile_PreservesCriticalNoLock()
    {
        var s = new AnalyzerSettings { Profile = AnalysisProfile.Reporting };
        Assert.Equal(RiskLevel.Critical, RuleLevel.Resolve(RiskLevel.Critical, s, "NOLOCK"));
        Assert.Equal(RiskLevel.Info, RuleLevel.Resolve(RiskLevel.Warning, s, "NOLOCK"));
        Assert.Equal(RiskLevel.Info, RuleLevel.Resolve(RiskLevel.Info, s, "NOLOCK"));
    }

    [Fact]
    public void RuleLevel_RuleSeverityOverrides_MatchesCaseInsensitivelyAndIgnoresUndefinedEnumValues()
    {
        var caseInsensitive = new AnalyzerSettings
        {
            RuleSeverityOverrides = new() { ["nolock"] = RiskLevel.Warning },
        };
        Assert.Equal(RiskLevel.Warning, RuleLevel.Resolve(RiskLevel.Info, caseInsensitive, "NOLOCK"));

        var ordinalDictionary = new AnalyzerSettings
        {
            RuleSeverityOverrides = new Dictionary<string, RiskLevel>(StringComparer.Ordinal)
            {
                ["select_star"] = RiskLevel.Critical,
            },
        };
        Assert.Equal(RiskLevel.Critical, RuleLevel.Resolve(RiskLevel.Warning, ordinalDictionary, "SELECT_STAR"));

        var undefinedEnum = new AnalyzerSettings
        {
            RuleSeverityOverrides = new() { ["NOLOCK"] = (RiskLevel)999 },
        };
        Assert.Equal(RiskLevel.Warning, RuleLevel.Resolve(RiskLevel.Warning, undefinedEnum, "NOLOCK"));

        var nullOverrides = new AnalyzerSettings { RuleSeverityOverrides = null! };
        Assert.Equal(RiskLevel.Warning, RuleLevel.Resolve(RiskLevel.Warning, nullOverrides, "SELECT_STAR"));
        Assert.Equal(RiskLevel.Info, RuleLevel.Resolve(RiskLevel.Info, caseInsensitive, string.Empty));
    }
}
