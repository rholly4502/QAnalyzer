using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class NullComparisonRuleTests
{
    private readonly NullComparisonRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("NULL_COMPARISON", _rule.Id);
        Assert.Equal("Perbandingan eksplisit dengan NULL", _rule.Title);
        Assert.Equal(RiskLevel.Critical, _rule.Level);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULL = DeletedAt;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULL = NULL;")]
    [InlineData("UPDATE dbo.Orders SET Flag = 1 WHERE DeletedAt = NULL;")]
    [InlineData("DELETE FROM dbo.Orders WHERE DeletedAt = NULL;")]
    [InlineData("SELECT o.Id FROM dbo.Orders o JOIN dbo.Customers c ON o.CustomerId = c.Id AND c.DeletedAt = NULL;")]
    [InlineData("SELECT CustomerId FROM dbo.Orders GROUP BY CustomerId HAVING MAX(DeletedAt) = NULL;")]
    [InlineData("IF @DeletedAt = NULL SELECT 1;")]
    [InlineData("WHILE @DeletedAt = NULL BREAK;")]
    [InlineData("SELECT CASE WHEN DeletedAt = NULL THEN 1 ELSE 0 END FROM dbo.Orders;")]
    [InlineData("SELECT IIF(DeletedAt = NULL, 1, 0) FROM dbo.Orders;")]
    [InlineData("WITH Recent AS (SELECT Id, DeletedAt FROM dbo.Orders) SELECT Id FROM Recent WHERE DeletedAt = NULL;")]
    [InlineData("CREATE PROCEDURE dbo.sp_GetDeleted AS BEGIN SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL; END;")]
    [InlineData("SELECT Id FROM dbo.Customers c WHERE EXISTS (SELECT 1 FROM dbo.Orders o WHERE o.CustomerId = c.Id AND o.DeletedAt = NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NOT (DeletedAt = NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt = (NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt = CAST(NULL AS DATETIME);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt = TRY_CAST(NULL AS DATETIME);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt = TRY_CONVERT(DATETIME, NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt = ISNULL(NULL, NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt = COALESCE(NULL, NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount = -NULL;")]
    public void Evaluate_EqualityWithNullUnderDefaultAnsiNulls_FlagsCriticalWithIsNullSuggestion(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal("Perbandingan eksplisit dengan NULL", diag.Title);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("NULL", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt <> NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt != NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULL <> DeletedAt;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt <> CONVERT(VARCHAR(10), NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NOT (DeletedAt <> NULL);")]
    public void Evaluate_InequalityWithNullUnderDefaultAnsiNulls_FlagsCriticalWithIsNotNullSuggestion(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("NULL", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NOT NULL", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount < NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount <= NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount > NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount >= NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount !< NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount !> NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULL < Amount;")]
    [InlineData("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE Amount < NULL;")]
    [InlineData("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE Amount >= NULL;")]
    public void Evaluate_OrderedComparisonsWithNull_AlwaysFlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("UNKNOWN", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Name LIKE NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Name NOT LIKE NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULL LIKE 'A%';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Name LIKE 'A%' ESCAPE NULL;")]
    [InlineData("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE Name LIKE NULL;")]
    [InlineData("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE Name NOT LIKE NULL;")]
    public void Evaluate_LikeOrNotLikeWithNull_AlwaysFlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("LIKE", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IS NULL", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount BETWEEN NULL AND 100;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount BETWEEN 1 AND NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULL BETWEEN 1 AND 100;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULL NOT BETWEEN 1 AND 100;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount NOT BETWEEN NULL AND NULL;")]
    public void Evaluate_BetweenOrFullNullNotBetween_FlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("BETWEEN", diag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount NOT BETWEEN NULL AND 100;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Amount NOT BETWEEN 1 AND NULL;")]
    public void Evaluate_NotBetweenWithSingleNullBound_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("NOT BETWEEN", diag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_SimpleCaseWhenNull_FlagsCriticalEvenUnderAnsiNullsOff()
    {
        const string sql = """
            SET ANSI_NULLS OFF;
            SELECT CASE Status
                       WHEN NULL THEN 'Empty'
                       WHEN 'A' THEN 'Active'
                       WHEN (NULL) THEN 'AlsoDead'
                       ELSE Status
                   END
            FROM dbo.Orders;
            """;

        var diagnostics = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal("NULL_COMPARISON", d.RuleId);
            Assert.Equal(RiskLevel.Critical, d.Level);
            Assert.Contains("CASE", d.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("IS NULL", d.Suggestion, StringComparison.Ordinal);
        });
        Assert.Equal(3, diagnostics[0].Line);
        Assert.Equal(5, diagnostics[1].Line);
    }

    [Theory]
    [InlineData("SELECT CASE NULL WHEN 1 THEN 'A' ELSE 'B' END FROM dbo.Orders;")]
    [InlineData("SET ANSI_NULLS OFF; SELECT CASE NULL WHEN 1 THEN 'A' WHEN NULL THEN 'B' ELSE 'C' END FROM dbo.Orders;")]
    public void Evaluate_SimpleCaseInputNull_ReportsOnceOnCaseNodeAsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("CASE", diag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;", "IS NULL")]
    [InlineData("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt <> NULL;", "IS NOT NULL")]
    [InlineData("SET ANSI_NULLS, QUOTED_IDENTIFIER OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;", "IS NULL")]
    [InlineData("SET ANSI_DEFAULTS OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;", "IS NULL")]
    public void Evaluate_EqualityWithNullUnderAnsiNullsOff_DowngradesToWarning(string sql, string expectedSuggestion)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("ANSI_NULLS OFF", diag.Message, StringComparison.Ordinal);
        Assert.Contains(expectedSuggestion, diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SET ANSI_NULLS OFF; SET ANSI_NULLS ON; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;")]
    [InlineData("SET ANSI_NULLS OFF; SET ANSI_DEFAULTS ON; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;")]
    [InlineData("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt = CAST(NULL AS DATETIME);")]
    public void Evaluate_AnsiNullsReEnabledOrCastNullUnderAnsiNullsOff_FlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NULL_COMPARISON", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Fact]
    public void Evaluate_CrossBatchPersistenceAndRoutineCreationTimeCapture_RespectsBatchAndProcBoundaries()
    {
        const string crossBatchSql = """
            SET ANSI_NULLS OFF
            GO
            CREATE PROCEDURE dbo.LegacyProc AS
                SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;
            GO
            SET ANSI_NULLS ON
            GO
            SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;
            """;

        var crossBatchDiagnostics = _rule.Evaluate(crossBatchSql).ToList();
        Assert.Equal(2, crossBatchDiagnostics.Count);
        Assert.Equal(RiskLevel.Warning, crossBatchDiagnostics[0].Level);
        Assert.Equal(RiskLevel.Critical, crossBatchDiagnostics[1].Level);

        const string innerProcSetSql = """
            CREATE PROCEDURE dbo.ProcWithInnerSet AS
            BEGIN
                SET ANSI_NULLS OFF;
                SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;
            END;
            """;

        var innerDiag = Assert.Single(_rule.Evaluate(innerProcSetSql));
        Assert.Equal(RiskLevel.Critical, innerDiag.Level);
    }

    [Fact]
    public void Evaluate_ControlFlowScoping_MergesAnsiNullsOffConservatively()
    {
        const string singleBranchSql = """
            IF @flag = 1
                SET ANSI_NULLS OFF;
            SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;
            """;

        var singleBranchDiag = Assert.Single(_rule.Evaluate(singleBranchSql));
        Assert.Equal(RiskLevel.Critical, singleBranchDiag.Level);

        const string bothBranchesSql = """
            IF @flag = 1
                SET ANSI_NULLS OFF;
            ELSE
                SET ANSI_NULLS OFF;
            SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;
            """;

        var bothBranchesDiag = Assert.Single(_rule.Evaluate(bothBranchesSql));
        Assert.Equal(RiskLevel.Warning, bothBranchesDiag.Level);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt IS NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt IS NOT NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt IS DISTINCT FROM NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DeletedAt IS NOT DISTINCT FROM NULL;")]
    [InlineData("SET @x = NULL;")]
    [InlineData("DECLARE @x INT = NULL;")]
    [InlineData("UPDATE dbo.Orders SET DeletedAt = NULL WHERE Id = 1;")]
    [InlineData("SELECT @x = NULL;")]
    [InlineData("EXEC dbo.p @x = NULL;")]
    [InlineData("INSERT INTO dbo.T (Col) VALUES (NULL);")]
    [InlineData("CREATE PROCEDURE dbo.p @x INT = NULL AS SELECT Id FROM dbo.Orders WHERE CustomerId = @x;")]
    [InlineData("SELECT CASE WHEN Status IS NULL THEN 'Empty' ELSE Status END FROM dbo.Orders;")]
    [InlineData("SELECT CASE Status WHEN 'A' THEN NULL ELSE 'B' END FROM dbo.Orders;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Col = ISNULL(@a, NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Col = COALESCE(@a, NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status IN ('A', NULL);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status NOT IN ('A', NULL);")]
    [InlineData("-- SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;\nSELECT Id FROM dbo.Orders;")]
    [InlineData("/* WHERE DeletedAt <> NULL */\nSELECT Id FROM dbo.Orders;")]
    [InlineData("SELECT 'WHERE DeletedAt = NULL' AS txt;")]
    public void Evaluate_SafeNullChecksAssignmentsCommentsAndLiterals_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_ReportsAccurateOneBasedLineAndColumn()
    {
        const string sql =
            "SELECT Id\n" +
            "FROM dbo.Orders\n" +
            "WHERE CustomerId = NULL\n" +
            "  AND CASE Status\n" +
            "          WHEN NULL THEN 1\n" +
            "          ELSE 0\n" +
            "      END = 1;";

        var diagnostics = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(3, diagnostics[0].Line);
        Assert.Equal(7, diagnostics[0].Column);
        Assert.Equal(5, diagnostics[1].Line);
        Assert.Equal(11, diagnostics[1].Column);
    }

    [Fact]
    public void Evaluate_HandlesEmptyNullAndMalformedSqlGracefully()
    {
        Assert.Empty(_rule.Evaluate(string.Empty));
        Assert.Empty(_rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => _rule.Evaluate("SELECT Id FROM dbo.Orders WHERE col =").ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void SqlAnalyzer_CombinesWithOtherRulesAndRespectsProfileAndOverrides()
    {
        const string sql = "SELECT * FROM dbo.Orders WHERE DeletedAt = NULL;";

        var defaultAnalyzer = new SqlAnalyzer(SqlAnalyzer.CreateDefaultRules());
        var defaultReport = defaultAnalyzer.Analyze(sql);
        Assert.True(defaultReport.HasCritical);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "NULL_COMPARISON" && d.Level == RiskLevel.Critical);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "SELECT_STAR" && d.Level == RiskLevel.Warning);

        var reportingAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });
        var reportingCritical = reportingAnalyzer.Analyze(sql);
        Assert.True(reportingCritical.HasCritical);
        Assert.Equal(RiskLevel.Critical, Assert.Single(reportingCritical.Diagnostics).Level);

        var reportingWarning = reportingAnalyzer.Analyze("SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;");
        Assert.False(reportingWarning.HasCritical);
        Assert.Equal(RiskLevel.Warning, Assert.Single(reportingWarning.Diagnostics).Level);

        var overriddenAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["NULL_COMPARISON"] = RiskLevel.Info,
                },
            });
        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.False(overriddenReport.HasCritical);
        Assert.Equal(RiskLevel.Info, Assert.Single(overriddenReport.Diagnostics).Level);
    }
}
