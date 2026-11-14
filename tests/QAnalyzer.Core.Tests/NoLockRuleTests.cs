using QAnalyzer.Core;

public sealed class NoLockRuleTests
{
    private readonly NoLockRule _rule = new();

    [Fact]
    public void NoLock_WithWithClause_EmitsWarning()
    {
        var diags = _rule.Evaluate("SELECT * FROM dbo.Orders WITH (NOLOCK);").ToList();
        Assert.Single(diags);
        Assert.Equal("NOLOCK", diags[0].RuleId);
        Assert.Equal(RiskLevel.Warning, diags[0].Level);
    }

    [Fact]
    public void NoLock_CompactWith_EmitsWarning()
    {
        var diags = _rule.Evaluate("SELECT * FROM dbo.Orders WITH(NOLOCK);").ToList();
        Assert.Single(diags);
    }

    [Fact]
    public void NoLock_BareHint_EmitsWarning()
    {
        var diags = _rule.Evaluate("SELECT * FROM dbo.Orders NOLOCK;").ToList();
        Assert.Single(diags);
    }

    [Fact]
    public void SafeQuery_NoDiagnostic()
    {
        Assert.Empty(_rule.Evaluate("SELECT * FROM dbo.Orders;"));
    }

    [Fact]
    public void ReportsLineAndColumn_OfMatch()
    {
        var sql = "SELECT *\nFROM dbo.Orders WITH (NOLOCK);";
        var diags = _rule.Evaluate(sql).ToList();
        var d = Assert.Single(diags);
        Assert.Equal(2, d.Line);
        Assert.True(d.Column >= 1);
    }
}
