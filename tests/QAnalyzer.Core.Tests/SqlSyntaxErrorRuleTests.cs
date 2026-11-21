using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class SqlSyntaxErrorRuleTests
{
    private readonly SqlSyntaxErrorRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndCriticalLevel()
    {
        Assert.Equal("SQL_SYNTAX_ERROR", _rule.Id);
        Assert.Equal("Kesalahan sintaks T-SQL", _rule.Title);
        Assert.Equal(RiskLevel.Critical, _rule.Level);
    }

    [Theory]
    [InlineData("SELECT * FORM dbo.Orders;")]
    [InlineData("UPDATE dbo.Orders SET Status = ;")]
    [InlineData("DELETE FROM dbo.Orders WHERE Id =")]
    [InlineData("SELECT (Id FROM dbo.Orders;")]
    [InlineData("SELECT 'unclosed string FROM dbo.Orders;")]
    [InlineData("/* unclosed comment SELECT 1;")]
    public void Evaluate_SyntaxErrors_FlagsCriticalWithPositionAndGuidance(string sql)
    {
        var diags = _rule.Evaluate(sql).ToList();

        Assert.NotEmpty(diags);
        Assert.All(diags, d =>
        {
            Assert.Equal("SQL_SYNTAX_ERROR", d.RuleId);
            Assert.Equal("Kesalahan sintaks T-SQL", d.Title);
            Assert.Equal(RiskLevel.Critical, d.Level);
            Assert.False(string.IsNullOrWhiteSpace(d.Message));
            Assert.False(string.IsNullOrWhiteSpace(d.Suggestion));
            Assert.DoesNotContain("\r", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", d.Message, StringComparison.Ordinal);
            Assert.True(d.Line >= 1, $"Expected Line >= 1, actual {d.Line}");
            Assert.True(d.Column >= 1, $"Expected Column >= 1, actual {d.Column}");
        });
    }

    [Fact]
    public void Evaluate_MultilineScript_ReportsAccurateOneBasedLineAndColumn()
    {
        const string sql = """
            SELECT Id FROM dbo.Orders;

                SELECT * FORM dbo.Users;
            """;

        var diags = _rule.Evaluate(sql).ToList();

        Assert.NotEmpty(diags);
        var diag = diags[0];
        Assert.Equal("SQL_SYNTAX_ERROR", diag.RuleId);
        Assert.Equal(3, diag.Line);
        Assert.True(diag.Column >= 5, $"Expected Column >= 5 on indented line, actual {diag.Column}");
    }

    [Theory]
    [InlineData("SELECT Id, Name FROM dbo.Customers WHERE IsActive = 1;")]
    [InlineData("SELECT o.Id, c.Name FROM dbo.Orders o INNER JOIN dbo.Customers c ON o.CustomerId = c.Id;")]
    [InlineData("WITH cte AS (SELECT Id, ParentId FROM dbo.Nodes WHERE ParentId IS NULL UNION ALL SELECT n.Id, n.ParentId FROM dbo.Nodes n JOIN cte c ON n.ParentId = c.Id) SELECT Id FROM cte;")]
    [InlineData("SELECT Id, ROW_NUMBER() OVER (PARTITION BY CustomerId ORDER BY OrderDate DESC) AS rn FROM dbo.Orders;")]
    [InlineData("MERGE dbo.Target AS t USING dbo.Source AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Val = s.Val WHEN NOT MATCHED THEN INSERT (Id, Val) VALUES (s.Id, s.Val);")]
    [InlineData("UPDATE t SET t.Status = 'Done' FROM dbo.Orders t WHERE t.Id = @id;")]
    [InlineData("DELETE TOP (100) FROM dbo.Orders WHERE CreatedAt < @cutoff;")]
    [InlineData("TRUNCATE TABLE dbo.Staging WITH (PARTITIONS (1 TO 2));")]
    [InlineData("SELECT TRY_CAST(j.[value] AS INT) AS Num FROM dbo.Payloads p CROSS APPLY OPENJSON(p.JsonBody) j FOR JSON PATH;")]
    [InlineData("CREATE PROCEDURE dbo.sp_Process AS BEGIN BEGIN TRY SELECT 1; END TRY BEGIN CATCH THROW; END CATCH END;")]
    [InlineData("SELECT 1;\nGO\nSELECT 2;")]
    [InlineData("-- komentar saja")]
    [InlineData("/* blok komentar saja */")]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void Evaluate_ValidTSqlCommentsAndBlank_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_NullSql_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());
    }

    [Fact]
    public void SqlAnalyzer_MultiBatchWithPartialValidity_DetectsBothAstRuleAndSyntaxErrorInSinglePass()
    {
        const string sql = """
            DELETE FROM dbo.Orders;
            GO
            SELECT * FORM dbo.Users;
            """;

        var analyzer = new SqlAnalyzer([
            _rule,
            new DeleteWithoutWhereRule(),
            new SelectStarRule(),
        ]);

        var report = analyzer.Analyze(sql);

        Assert.True(report.HasCritical);
        var deleteDiag = Assert.Single(report.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE");
        Assert.Equal(1, deleteDiag.Line);

        var syntaxDiag = report.Diagnostics.FirstOrDefault(d => d.RuleId == "SQL_SYNTAX_ERROR");
        Assert.NotNull(syntaxDiag);
        Assert.Equal(3, syntaxDiag.Line);
        Assert.Equal(RiskLevel.Critical, syntaxDiag.Level);
    }

    [Fact]
    public void Evaluate_ManySyntaxErrors_CapsDiagnosticsAtMaxReportedErrors()
    {
        var batches = Enumerable.Range(1, SqlSyntaxErrorRule.MaxReportedErrors + 10)
            .Select(i => $"SELECT * FORM dbo.Table_{i};");
        var sql = string.Join("\nGO\n", batches);

        var diags = _rule.Evaluate(sql).ToList();

        Assert.Equal(SqlSyntaxErrorRule.MaxReportedErrors, diags.Count);
        Assert.All(diags, d => Assert.Equal("SQL_SYNTAX_ERROR", d.RuleId));
    }

    [Fact]
    public void SqlAnalyzer_RespectsReportingProfileAndRuleSeverityOverrides()
    {
        const string sql = "SELECT * FORM dbo.Orders;";

        var reportingAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });
        var reportingReport = reportingAnalyzer.Analyze(sql);
        Assert.True(reportingReport.HasCritical);
        Assert.All(reportingReport.Diagnostics, d => Assert.Equal(RiskLevel.Critical, d.Level));

        var overriddenAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["SQL_SYNTAX_ERROR"] = RiskLevel.Warning,
                },
            });
        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.False(overriddenReport.HasCritical);
        Assert.NotEmpty(overriddenReport.Diagnostics);
        Assert.All(overriddenReport.Diagnostics, d => Assert.Equal(RiskLevel.Warning, d.Level));
    }
}
