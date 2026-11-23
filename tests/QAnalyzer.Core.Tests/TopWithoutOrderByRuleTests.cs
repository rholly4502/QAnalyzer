using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class TopWithoutOrderByRuleTests
{
    private readonly TopWithoutOrderByRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("TOP_WITHOUT_ORDER_BY", _rule.Id);
        Assert.Equal("SELECT TOP tanpa ORDER BY deterministik", _rule.Title);
        Assert.Equal(RiskLevel.Warning, _rule.Level);
    }

    [Theory]
    [InlineData("SELECT TOP 10 Id, Name FROM dbo.Orders;")]
    [InlineData("SELECT TOP (10) Id, Name FROM dbo.Orders WHERE Status = 'OPEN';")]
    [InlineData("SELECT TOP (@limit) Id FROM dbo.Orders;")]
    [InlineData("SELECT TOP (25) PERCENT Id FROM dbo.Orders;")]
    [InlineData("SELECT DISTINCT TOP (5) CustomerId FROM dbo.Orders;")]
    [InlineData("SELECT TOP (1) @lastId = Id FROM dbo.Orders WHERE CustomerId = @cid;")]
    [InlineData("SELECT TOP (5) Id, ROW_NUMBER() OVER (ORDER BY Id) AS Rn FROM dbo.Orders;")]
    [InlineData("WITH Recent AS (SELECT TOP (10) Id FROM dbo.Orders) SELECT Id FROM Recent ORDER BY Id;")]
    [InlineData("SELECT Id FROM (SELECT TOP (5) Id FROM dbo.Orders) AS d ORDER BY d.Id;")]
    [InlineData("UPDATE dbo.Accounts SET LastOrderId = (SELECT TOP 1 Id FROM dbo.Orders o WHERE o.AccountId = dbo.Accounts.Id) WHERE Id = 1;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Id IN (SELECT TOP (10) OrderId FROM dbo.OrderItems);")]
    [InlineData("INSERT INTO dbo.TopOrders (Id) SELECT TOP (10) Id FROM dbo.Orders;")]
    [InlineData("SELECT TOP (10) Id FROM dbo.Orders UNION ALL SELECT Id FROM dbo.ArchivedOrders ORDER BY Id;")]
    [InlineData("(SELECT TOP (10) Id FROM dbo.Orders) ORDER BY Id;")]
    public void Evaluate_SelectTopWithoutOrderBy_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("TOP_WITHOUT_ORDER_BY", diag.RuleId);
        Assert.Equal("SELECT TOP tanpa ORDER BY deterministik", diag.Title);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("tidak deterministik", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.vOrders AS SELECT TOP (100) PERCENT Id, Name FROM dbo.Orders ORDER BY Name;")]
    [InlineData("ALTER VIEW dbo.vOrders AS SELECT TOP 100 PERCENT Id FROM dbo.Orders ORDER BY Id DESC;")]
    [InlineData("CREATE OR ALTER VIEW dbo.vOrders AS SELECT TOP (100.0) PERCENT Id FROM dbo.Orders ORDER BY Id;")]
    [InlineData("CREATE FUNCTION dbo.fnSortedOrders() RETURNS TABLE AS RETURN (SELECT TOP (100) PERCENT Id FROM dbo.Orders ORDER BY Id);")]
    [InlineData("WITH Sorted AS (SELECT TOP (100) PERCENT Id FROM dbo.Orders ORDER BY Id) SELECT Id FROM Sorted;")]
    [InlineData("SELECT Id FROM (SELECT TOP 100 PERCENT Id FROM dbo.Orders ORDER BY Id) AS sub;")]
    [InlineData("INSERT INTO dbo.Stage (Id) SELECT TOP (100) PERCENT Id FROM dbo.Orders ORDER BY Id;")]
    [InlineData("SELECT TOP (100) PERCENT Id INTO #Sorted FROM dbo.Orders ORDER BY Id;")]
    public void Evaluate_Top100PercentWithOrderByInNestedOrViewContext_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("TOP_WITHOUT_ORDER_BY", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("TOP (100) PERCENT", diag.Message, StringComparison.Ordinal);
        Assert.Contains("diabaikan oleh query optimizer", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SELECT terluar", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_MultipleUnsafeTopClauses_FlagsAllInSourceOrder()
    {
        const string sql = """
            WITH cte AS (
                SELECT TOP (100) PERCENT Id, CreatedAt FROM dbo.Orders ORDER BY CreatedAt DESC
            )
            SELECT TOP (5) Id FROM cte;
            """;

        var diags = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diags.Count);
        Assert.Equal(2, diags[0].Line);
        Assert.Contains("TOP (100) PERCENT", diags[0].Message, StringComparison.Ordinal);
        Assert.Equal(4, diags[1].Line);
        Assert.Contains("tidak deterministik", diags[1].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT TOP (10) Id, Name FROM dbo.Orders ORDER BY CreatedAt DESC;")]
    [InlineData("SELECT TOP 1 Id FROM dbo.Orders ORDER BY Id ASC;")]
    [InlineData("SELECT TOP (10) WITH TIES Id, Score FROM dbo.Scores ORDER BY Score DESC;")]
    [InlineData("(SELECT TOP (10) Id FROM dbo.Orders ORDER BY Id DESC);")]
    [InlineData("SELECT Id FROM dbo.Customers c WHERE EXISTS (SELECT TOP (1) 1 FROM dbo.Orders o WHERE o.CustomerId = c.Id);")]
    [InlineData("SELECT Id FROM dbo.Customers c WHERE EXISTS (SELECT TOP 1 o.Id FROM dbo.Orders o WHERE o.CustomerId = c.Id);")]
    [InlineData("SELECT Id FROM dbo.Customers c WHERE NOT EXISTS (SELECT TOP (1) * FROM dbo.Orders o WHERE o.CustomerId = c.Id);")]
    [InlineData("SELECT TOP (1) 1;")]
    [InlineData("SELECT TOP (1) @val AS CurrentValue;")]
    [InlineData("SELECT TOP (1) 1 FROM dbo.Orders WHERE CustomerId = @id;")]
    [InlineData("SELECT TOP (1) @found = 1 FROM dbo.Orders WHERE CustomerId = @id;")]
    [InlineData("SELECT TOP (1) COUNT(*) FROM dbo.Orders WHERE Status = 'OPEN';")]
    [InlineData("SELECT TOP (1) MIN(CreatedAt), MAX(CreatedAt) FROM dbo.Orders;")]
    [InlineData("DELETE TOP (1000) FROM dbo.AuditLogs WHERE CreatedAt < @cutoff;")]
    [InlineData("UPDATE TOP (500) dbo.Orders SET Processed = 1 WHERE Processed = 0;")]
    [InlineData("SELECT TOP (100) PERCENT Id FROM dbo.Orders ORDER BY Id;")]
    [InlineData("SELECT TOP (100) PERCENT Id FROM dbo.Orders;")]
    [InlineData("SELECT (SELECT TOP (100) PERCENT Id FROM dbo.Orders ORDER BY Id FOR XML PATH('r')) AS XmlPayload;")]
    [InlineData("-- SELECT TOP 10 Id FROM dbo.Orders;\nSELECT Id FROM dbo.Orders;")]
    [InlineData("/* SELECT TOP (5) Id FROM dbo.Orders; */\nSELECT Id FROM dbo.Orders;")]
    [InlineData("SELECT 'SELECT TOP 10 Id FROM dbo.Orders' AS QueryText;")]
    public void Evaluate_DeterministicSafeTopExistsAggregateDmlCommentsAndLiterals_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_ReportsAccurateOneBasedLineAndColumn()
    {
        const string sql =
            "SELECT\n" +
            "    TOP (10) Id,\n" +
            "    Name\n" +
            "FROM dbo.Orders;";

        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal(2, diag.Line);
        Assert.Equal(5, diag.Column);
    }

    [Fact]
    public void Evaluate_HandlesNullEmptyWhitespaceAndMalformedSqlGracefully()
    {
        Assert.Empty(_rule.Evaluate(string.Empty));
        Assert.Empty(_rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());

        var ex = Record.Exception(() => _rule.Evaluate("SELECT TOP (10) FROM").ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void SqlAnalyzer_CombinesWithOtherRulesAndRespectsProfileAndOverrides()
    {
        const string sql = "SELECT TOP (10) * FROM dbo.Orders;";

        var defaultAnalyzer = new SqlAnalyzer(SqlAnalyzer.CreateDefaultRules());
        var defaultReport = defaultAnalyzer.Analyze(sql);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "SELECT_STAR" && d.Level == RiskLevel.Warning);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "TOP_WITHOUT_ORDER_BY" && d.Level == RiskLevel.Warning);

        var reportingAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });
        var reportingReport = reportingAnalyzer.Analyze(sql);
        var reportingDiag = Assert.Single(reportingReport.Diagnostics);
        Assert.Equal(RiskLevel.Warning, reportingDiag.Level);

        var overriddenAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["TOP_WITHOUT_ORDER_BY"] = RiskLevel.Critical,
                },
            });
        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.True(overriddenReport.HasCritical);
        var overriddenDiag = Assert.Single(overriddenReport.Diagnostics);
        Assert.Equal(RiskLevel.Critical, overriddenDiag.Level);
    }
}
