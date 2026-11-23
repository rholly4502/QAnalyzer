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

    [Fact]
    public void Analyze_NullSql_ThrowsArgumentNullException()
    {
        var analyzer = new SqlAnalyzer([]);
        var ex = Assert.Throws<ArgumentNullException>(() => analyzer.Analyze(null!));
        Assert.Equal("sql", ex.ParamName);
    }

    [Fact]
    public void CreateDefaultRules_IncludesAllBuiltInRulesWithoutDuplicateIds()
    {
        var rules = SqlAnalyzer.CreateDefaultRules();
        var ids = rules.Select(r => r.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("SQL_SYNTAX_ERROR", ids);
        Assert.Contains("SELECT_STAR", ids);
        Assert.Contains("NOLOCK", ids);
        Assert.Contains("UPDATE_NO_WHERE", ids);
        Assert.Contains("DELETE_NO_WHERE", ids);
        Assert.Contains("TRUNCATE_TABLE", ids);
        Assert.Contains("NON_SARGABLE", ids);
        Assert.Contains("DYNAMIC_SQL", ids);
        Assert.Contains("CURSOR_USAGE", ids);
        Assert.Contains("NOT_IN_SUBQUERY", ids);
        Assert.Contains("NULL_COMPARISON", ids);
        Assert.Contains("UNSPECIFIED_TYPE_LENGTH", ids);
        Assert.Contains("TOP_WITHOUT_ORDER_BY", ids);
        Assert.Contains("INSERT_WITHOUT_COLUMNS", ids);
    }
}
