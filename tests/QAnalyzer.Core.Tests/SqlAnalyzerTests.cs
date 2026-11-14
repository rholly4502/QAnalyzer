using QAnalyzer.Core;

public sealed class SqlAnalyzerTests
{
    private const string SafeQuery = "SELECT id, name FROM dbo.Customers WHERE id = @id;";

    [Fact]
    public void Analyze_WithNoRules_ReturnsEmptyReport()
    {
        var analyzer = new SqlAnalyzer([]);
        var report = analyzer.Analyze(SafeQuery);
        Assert.Equal(0, report.Total);
        Assert.False(report.HasCritical);
    }

    [Fact]
    public void Analyze_UsesRegisteredRule()
    {
        var rule = new FakeRule();
        var analyzer = new SqlAnalyzer([rule]);
        var report = analyzer.Analyze(SafeQuery);
        Assert.Equal(1, report.Total);
        Assert.True(report.Diagnostics[0].RuleId == "FAKE");
    }

    private sealed class FakeRule : ISqlRule
    {
        public string Id => "FAKE";
        public string Title => "Fake";
        public RiskLevel Level => RiskLevel.Warning;
        public IEnumerable<Diagnostic> Evaluate(string sql) =>
            [ new Diagnostic(Level, Id, Title, "tandai semua") ];
    }
}
