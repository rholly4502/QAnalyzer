using QAnalyzer.Core;

public sealed class SelectStarRuleTests
{
    private readonly SelectStarRule _rule = new();

    [Fact]
    public void SelectStar_EmitsWarning()
    {
        var diags = _rule.Evaluate("SELECT * FROM dbo.Orders;").ToList();
        Assert.Single(diags);
        Assert.Equal("SELECTSTAR", diags[0].RuleId);
        Assert.Equal(RiskLevel.Warning, diags[0].Level);
    }

    [Fact]
    public void SelectStar_WithLeadingStar_EmitsWarning()
    {
        var diags = _rule.Evaluate("SELECT *, id FROM dbo.Orders;").ToList();
        Assert.Single(diags);
    }

    [Fact]
    public void SafeColumnList_NoDiagnostic()
    {
        Assert.Empty(_rule.Evaluate("SELECT id, name FROM dbo.Orders;"));
    }

    [Fact]
    public void QualifiedStar_NoDiagnostic()
    {
        Assert.Empty(_rule.Evaluate("SELECT o.* FROM dbo.Orders AS o;"));
    }

    [Fact]
    public void CountStar_NoDiagnostic()
    {
        Assert.Empty(_rule.Evaluate("SELECT COUNT(*) FROM dbo.Orders;"));
    }

    [Fact]
    public void ReportsLineAndColumn_OfMatch()
    {
        var sql = "SELECT id\nFROM dbo.Orders;\nSELECT * FROM dbo.Orders;";
        var diags = _rule.Evaluate(sql).ToList();
        var d = Assert.Single(diags);
        Assert.Equal(3, d.Line);
        Assert.True(d.Column >= 1);
    }
}
