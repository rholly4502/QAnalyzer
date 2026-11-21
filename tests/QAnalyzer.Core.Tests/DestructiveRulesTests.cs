using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class DestructiveRulesTests
{
    private readonly DeleteWithoutWhereRule _deleteRule = new();
    private readonly TruncateTableRule _truncateRule = new();

    // --- DeleteWithoutWhereRule ---

    [Fact]
    public void DeleteWithoutWhereRule_Metadata_HasExpectedIdTitleAndCriticalLevel()
    {
        Assert.Equal("DELETE_NO_WHERE", _deleteRule.Id);
        Assert.Equal("DELETE tanpa klausa WHERE", _deleteRule.Title);
        Assert.Equal(RiskLevel.Critical, _deleteRule.Level);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Orders;")]
    [InlineData("DELETE dbo.Orders;")]
    [InlineData("DELETE TOP (100) FROM dbo.Orders;")]
    [InlineData("DELETE o FROM dbo.Orders o;")]
    public void DeleteWithoutWhereRule_FlagsBareDeleteVariantsAsCritical(string sql)
    {
        var diag = Assert.Single(_deleteRule.Evaluate(sql));

        Assert.Equal("DELETE_NO_WHERE", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.False(string.IsNullOrWhiteSpace(diag.Message));
        Assert.False(string.IsNullOrWhiteSpace(diag.Suggestion));
    }

    [Fact]
    public void DeleteWithoutWhereRule_ReportsAccurateOneBasedLineAndColumnIncludingCte()
    {
        const string multiLineSql = "SELECT 1;\n  DELETE FROM dbo.Orders;";
        var multiLineDiag = Assert.Single(_deleteRule.Evaluate(multiLineSql));
        Assert.Equal(2, multiLineDiag.Line);
        Assert.Equal(3, multiLineDiag.Column);

        const string cteSql = """
            WITH cte AS (SELECT Id FROM dbo.Orders)
            DELETE FROM cte;
            """;
        var cteDiag = Assert.Single(_deleteRule.Evaluate(cteSql));
        Assert.Equal(2, cteDiag.Line);
        Assert.Equal(1, cteDiag.Column);
    }

    [Fact]
    public void DeleteWithoutWhereRule_FlagsOnlyBareDeleteInMultiStatementAndAcrossGoBatches()
    {
        const string multiStmtSql = """
            DELETE FROM dbo.T1;
            DELETE FROM dbo.T2 WHERE Id = 1;
            """;
        var multiDiag = Assert.Single(_deleteRule.Evaluate(multiStmtSql));
        Assert.Equal("DELETE_NO_WHERE", multiDiag.RuleId);
        Assert.Equal(1, multiDiag.Line);

        const string goBatchSql = """
            DELETE FROM dbo.T1 WHERE Id = 1
            GO
            DELETE FROM dbo.T2;
            """;
        var goDiag = Assert.Single(_deleteRule.Evaluate(goBatchSql));
        Assert.Equal(RiskLevel.Critical, goDiag.Level);
        Assert.Equal(3, goDiag.Line);
    }

    [Theory]
    [InlineData("IF @purge = 1 BEGIN DELETE FROM dbo.Audit; END;")]
    [InlineData("CREATE TRIGGER dbo.trg_Audit ON dbo.Orders AFTER DELETE AS BEGIN DELETE FROM dbo.Audit; END;")]
    [InlineData("INSERT INTO dbo.Archive (Id) SELECT Id FROM (DELETE FROM dbo.Orders OUTPUT DELETED.Id) AS d;")]
    public void DeleteWithoutWhereRule_FlagsBareDeleteInControlFlowTriggerAndComposableDml(string sql)
    {
        var diag = Assert.Single(_deleteRule.Evaluate(sql));

        Assert.Equal("DELETE_NO_WHERE", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Orders WHERE Id = @id;")]
    [InlineData("DELETE o FROM dbo.Orders o WHERE o.Status = 'Expired';")]
    [InlineData("DELETE FROM dbo.Orders WHERE CURRENT OF cur;")]
    [InlineData("INSERT INTO dbo.Archive (Id) SELECT Id FROM (DELETE FROM dbo.Orders OUTPUT DELETED.Id WHERE Id = 1) AS d;")]
    public void DeleteWithoutWhereRule_SkipsDeleteWithWhereClause(string sql)
    {
        Assert.Empty(_deleteRule.Evaluate(sql));
    }

    [Theory]
    [InlineData("MERGE dbo.T AS t USING dbo.S AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;")]
    [InlineData("CREATE TABLE dbo.Child (Id INT, ParentId INT REFERENCES dbo.Parent(Id) ON DELETE CASCADE);")]
    [InlineData("CREATE TRIGGER dbo.trg_Orders ON dbo.Orders AFTER DELETE AS BEGIN SELECT 1; END;")]
    public void DeleteWithoutWhereRule_DoesNotFlagNonDeleteSpecificationSyntax(string sql)
    {
        Assert.Empty(_deleteRule.Evaluate(sql));
    }

    [Theory]
    [InlineData("-- DELETE FROM dbo.Orders;")]
    [InlineData("/* DELETE FROM dbo.Orders; */ SELECT 1;")]
    [InlineData("SELECT 'DELETE FROM dbo.Orders' AS msg;")]
    public void DeleteWithoutWhereRule_IgnoresCommentsAndStringLiterals(string sql)
    {
        Assert.Empty(_deleteRule.Evaluate(sql));
    }

    [Fact]
    public void DeleteWithoutWhereRule_HandlesEmptyNullAndMalformedSqlGracefully()
    {
        Assert.Empty(_deleteRule.Evaluate(string.Empty));
        Assert.Empty(_deleteRule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _deleteRule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => _deleteRule.Evaluate("DELETE FROM").ToList());
        Assert.Null(ex);
    }

    // --- TruncateTableRule ---

    [Fact]
    public void TruncateTableRule_Metadata_HasExpectedIdTitleAndCriticalLevel()
    {
        Assert.Equal("TRUNCATE_TABLE", _truncateRule.Id);
        Assert.Equal("TRUNCATE TABLE destruktif", _truncateRule.Title);
        Assert.Equal(RiskLevel.Critical, _truncateRule.Level);
    }

    [Theory]
    [InlineData("TRUNCATE TABLE dbo.Orders;", "dbo.Orders")]
    [InlineData("TRUNCATE TABLE Orders;", "Orders")]
    [InlineData("TRUNCATE TABLE [dbo].[Orders] WITH (PARTITIONS (1, 2 TO 4));", "dbo.Orders")]
    public void TruncateTableRule_FlagsTruncateVariantsWithTargetTableAndSuggestion(string sql, string expectedTable)
    {
        var diag = Assert.Single(_truncateRule.Evaluate(sql));

        Assert.Equal("TRUNCATE_TABLE", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains(expectedTable, diag.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(diag.Suggestion));
    }

    [Fact]
    public void TruncateTableRule_ReportsAccurateOneBasedLineAndColumnAcrossContexts()
    {
        const string multiLineSql = "SELECT 1;\n    TRUNCATE TABLE dbo.Orders;";
        var multiLineDiag = Assert.Single(_truncateRule.Evaluate(multiLineSql));
        Assert.Equal(2, multiLineDiag.Line);
        Assert.Equal(5, multiLineDiag.Column);

        const string goBatchSql = """
            SELECT 1
            GO
              TRUNCATE TABLE dbo.Staging;
            """;
        var goDiag = Assert.Single(_truncateRule.Evaluate(goBatchSql));
        Assert.Equal(3, goDiag.Line);
        Assert.Equal(3, goDiag.Column);

        const string procSql = """
            CREATE PROCEDURE dbo.sp_Reset
            AS
            BEGIN
                IF 1 = 1
                    TRUNCATE TABLE dbo.TempData;
            END;
            """;
        var procDiag = Assert.Single(_truncateRule.Evaluate(procSql));
        Assert.Equal(5, procDiag.Line);
        Assert.Equal(9, procDiag.Column);
    }

    [Theory]
    [InlineData("-- TRUNCATE TABLE dbo.Orders;")]
    [InlineData("/* TRUNCATE TABLE dbo.Orders; */ SELECT 1;")]
    [InlineData("SELECT 'TRUNCATE TABLE dbo.Orders' AS msg;")]
    [InlineData("SELECT [Truncate] FROM dbo.AuditLogs;")]
    public void TruncateTableRule_IgnoresCommentsLiteralsAndIdentifiersNamedTruncate(string sql)
    {
        Assert.Empty(_truncateRule.Evaluate(sql));
    }

    [Fact]
    public void TruncateTableRule_HandlesEmptyNullAndMalformedSqlGracefully()
    {
        Assert.Empty(_truncateRule.Evaluate(string.Empty));
        Assert.Empty(_truncateRule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _truncateRule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => _truncateRule.Evaluate("TRUNCATE TABLE").ToList());
        Assert.Null(ex);
    }

    // --- SqlAnalyzer Integration ---

    [Fact]
    public void SqlAnalyzer_KeepsCriticalUnderReportingProfile_AndRespectsOverrides()
    {
        const string sql = """
            DELETE FROM dbo.Orders;
            TRUNCATE TABLE dbo.AuditLog;
            """;

        var reportingAnalyzer = new SqlAnalyzer(
            [_deleteRule, _truncateRule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });

        var reportingReport = reportingAnalyzer.Analyze(sql);
        Assert.Equal(2, reportingReport.Total);
        Assert.True(reportingReport.HasCritical);
        Assert.Equal(RiskLevel.Critical, Assert.Single(reportingReport.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE").Level);
        Assert.Equal(RiskLevel.Critical, Assert.Single(reportingReport.Diagnostics, d => d.RuleId == "TRUNCATE_TABLE").Level);

        var overriddenAnalyzer = new SqlAnalyzer(
            [_deleteRule, _truncateRule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["DELETE_NO_WHERE"] = RiskLevel.Warning,
                    ["TRUNCATE_TABLE"] = RiskLevel.Warning,
                },
            });

        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.Equal(2, overriddenReport.Total);
        Assert.False(overriddenReport.HasCritical);
        Assert.Equal(RiskLevel.Warning, Assert.Single(overriddenReport.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE").Level);
        Assert.Equal(RiskLevel.Warning, Assert.Single(overriddenReport.Diagnostics, d => d.RuleId == "TRUNCATE_TABLE").Level);
    }
}
