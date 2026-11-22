using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class NotInSubqueryRuleTests
{
    private readonly NotInSubqueryRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("NOT_IN_SUBQUERY", _rule.Id);
        Assert.Equal("Predicate NOT IN berisiko jebakan NULL", _rule.Title);
        Assert.Equal(RiskLevel.Warning, _rule.Level);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);")]
    [InlineData("UPDATE dbo.Orders SET Flag = 1 WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);")]
    [InlineData("DELETE FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);")]
    [InlineData("SELECT o.Id FROM dbo.Orders o JOIN dbo.Customers c ON o.CustomerId = c.Id AND c.RegionId NOT IN (SELECT RegionId FROM dbo.BlockedRegions);")]
    [InlineData("SELECT CustomerId FROM dbo.Orders GROUP BY CustomerId HAVING CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);")]
    [InlineData("SELECT CASE WHEN CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist) THEN 1 ELSE 0 END FROM dbo.Orders;")]
    [InlineData("IF @CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist) SELECT 1;")]
    [InlineData("WITH ActiveOrders AS (SELECT Id, CustomerId FROM dbo.Orders) SELECT Id FROM ActiveOrders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);")]
    [InlineData("CREATE PROCEDURE dbo.sp_GetOrders AS BEGIN SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist); END;")]
    public void Evaluate_NullableSubqueryInVariousContexts_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NOT_IN_SUBQUERY", diag.RuleId);
        Assert.Equal("Predicate NOT IN berisiko jebakan NULL", diag.Title);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("NULL", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT EXISTS", diag.Suggestion, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NOT (CustomerId IN (SELECT CustomerId FROM dbo.Blacklist));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NOT CustomerId IN (SELECT CustomerId FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NOT (NOT (CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist)));")]
    public void Evaluate_NegatedInAndTripleNegationSubquery_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NOT_IN_SUBQUERY", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("NOT EXISTS", diag.Suggestion, StringComparison.Ordinal);
        Assert.Contains("IS NOT NULL", diag.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_NestedNotInInsideSubquery_ReportsBothWithAccuratePositions()
    {
        const string sql =
            "SELECT Id\n" +
            "FROM dbo.Orders\n" +
            "WHERE CustomerId NOT IN (\n" +
            "    SELECT b.CustomerId\n" +
            "    FROM dbo.Blacklist b\n" +
            "    WHERE b.GroupId NOT IN (SELECT g.Id FROM dbo.BlockedGroups g)\n" +
            ");";

        var diagnostics = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("NOT_IN_SUBQUERY", d.RuleId);
            Assert.Equal(RiskLevel.Warning, d.Level);
        });
        Assert.Equal(3, diagnostics[0].Line);
        Assert.Equal(7, diagnostics[0].Column);
        Assert.Equal(6, diagnostics[1].Line);
        Assert.Equal(11, diagnostics[1].Column);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN ('A', 'B', NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN ((NULL));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (1, CAST(NULL AS INT));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (1, CONVERT(INT, NULL));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (1, TRY_CAST(NULL AS INT));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (1, TRY_CONVERT(INT, NULL));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NOT (Status IN ('A', NULL));")]
    public void Evaluate_ExplicitNullInNotInValueList_FlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NOT_IN_SUBQUERY", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.False(string.IsNullOrWhiteSpace(diag.Message));
        Assert.Contains("NULL", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("three-valued logic", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(diag.Suggestion));
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT b.CustomerId FROM dbo.Blacklist b WHERE b.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist b WHERE b.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT b.CustomerId FROM dbo.Blacklist b WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE Blacklist.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE dbo.Blacklist.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT Blacklist.CustomerId FROM dbo.Blacklist WHERE dbo.Blacklist.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE NOT (CustomerId IS NULL));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT NULL AND IsActive = 1);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId = 1 OR CustomerId = 2);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId > 0);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId = 5);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId <> 0);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId LIKE 'A%');")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId BETWEEN 1 AND 10);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IN (1, 2));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist GROUP BY CustomerId HAVING CustomerId IS NOT NULL);")]
    public void Evaluate_NullGuardedSubqueryColumns_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT ISNULL(CustomerId, -1) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT COALESCE(CustomerId, 0) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Total NOT IN (SELECT COUNT(*) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Total NOT IN (SELECT COUNT_BIG(CustomerId) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (SELECT 1 FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (SELECT -1 FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Code NOT IN (SELECT 'A' FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CAST(1 AS BIGINT) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CONVERT(INT, 1) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (SELECT IIF(Status = 1, 1, 2) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (SELECT CASE WHEN Status = 1 THEN 1 ELSE 2 END FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE RowNum NOT IN (SELECT ROW_NUMBER() OVER (ORDER BY Id) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA WHERE CustomerId IS NOT NULL UNION SELECT CustomerId FROM dbo.BlacklistB WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA WHERE CustomerId IS NOT NULL UNION ALL SELECT CustomerId FROM dbo.BlacklistB WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA WHERE CustomerId IS NOT NULL EXCEPT SELECT CustomerId FROM dbo.BlacklistB);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA INTERSECT SELECT CustomerId FROM dbo.BlacklistB WHERE CustomerId IS NOT NULL);")]
    public void Evaluate_GuaranteedNonNullExpressionsAndSetOperations_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT NULL OR IsActive = 1);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE NOT (CustomerId IS NOT NULL));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE IsActive = 1);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE ISNULL(CustomerId, 1) > 0);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT DISTINCT FROM @x);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT b.CustomerId FROM dbo.A a LEFT JOIN dbo.B b ON a.Id = b.Id AND b.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT a.CustomerId FROM dbo.A a JOIN dbo.B b ON a.Id = b.Id WHERE b.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.A a JOIN dbo.B b ON a.Id = b.Id WHERE b.CustomerId IS NOT NULL);")]
    [InlineData("SELECT o.Id FROM dbo.Orders o WHERE o.CustomerId NOT IN (SELECT b.CustomerId FROM dbo.Blacklist b WHERE o.CustomerId IS NOT NULL);")]
    [InlineData("SELECT o.Id FROM dbo.Orders o WHERE o.CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist b WHERE o.CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT MAX(CustomerId) FROM dbo.Blacklist WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT NULLIF(CustomerId, 0) FROM dbo.Blacklist WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT TRY_CAST(CustomerId AS INT) FROM dbo.Blacklist WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT ISNULL(CustomerId, FallbackId) FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN (SELECT CASE WHEN Status = 1 THEN 1 END FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT NULL GROUP BY ROLLUP(CustomerId));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT NULL GROUP BY CUBE(CustomerId));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT NULL GROUP BY GROUPING SETS((CustomerId), ()));")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA WHERE CustomerId IS NOT NULL UNION SELECT CustomerId FROM dbo.BlacklistB);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA WHERE CustomerId IS NOT NULL UNION ALL SELECT CustomerId FROM dbo.BlacklistB);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA EXCEPT SELECT CustomerId FROM dbo.BlacklistB WHERE CustomerId IS NOT NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.BlacklistA INTERSECT SELECT CustomerId FROM dbo.BlacklistB);")]
    public void Evaluate_SubtleNullableTraps_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NOT_IN_SUBQUERY", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId IN (SELECT CustomerId FROM dbo.Blacklist);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status IN ('A', NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN ('A', 'B');")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (@a, @b);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NOT (CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist));")]
    [InlineData("SELECT Id FROM dbo.Orders o WHERE NOT EXISTS (SELECT 1 FROM dbo.Blacklist b WHERE b.CustomerId = o.CustomerId);")]
    [InlineData("SELECT Id FROM dbo.Orders o WHERE NOT EXISTS (SELECT 1 FROM dbo.Blacklist b WHERE b.CustomerId IN (SELECT CustomerId FROM dbo.Other));")]
    [InlineData("-- SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);\nSELECT Id FROM dbo.Orders;")]
    [InlineData("/* WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist) */\nSELECT Id FROM dbo.Orders;")]
    [InlineData("SELECT 'x NOT IN (SELECT y FROM t)' AS txt;")]
    public void Evaluate_SafeQueriesCommentsAndStringLiterals_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_HandlesEmptyNullAndMalformedSqlGracefully()
    {
        Assert.Empty(_rule.Evaluate(string.Empty));
        Assert.Empty(_rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => _rule.Evaluate("SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT").ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void SqlAnalyzer_CombinesWithOtherRulesAndRespectsProfileAndOverrides()
    {
        const string sql = "SELECT * FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);";

        var defaultAnalyzer = new SqlAnalyzer(SqlAnalyzer.CreateDefaultRules());
        var defaultReport = defaultAnalyzer.Analyze(sql);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "NOT_IN_SUBQUERY" && d.Level == RiskLevel.Warning);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "SELECT_STAR" && d.Level == RiskLevel.Warning);

        var reportingAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });
        var reportingReport = reportingAnalyzer.Analyze(sql);
        Assert.False(reportingReport.HasCritical);
        Assert.Equal(RiskLevel.Warning, Assert.Single(reportingReport.Diagnostics).Level);

        var overriddenAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["NOT_IN_SUBQUERY"] = RiskLevel.Critical,
                },
            });
        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.True(overriddenReport.HasCritical);
        Assert.Equal(RiskLevel.Critical, Assert.Single(overriddenReport.Diagnostics).Level);
    }
}
