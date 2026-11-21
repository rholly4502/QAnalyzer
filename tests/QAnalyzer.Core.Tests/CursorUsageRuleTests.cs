using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class CursorUsageRuleTests
{
    private readonly CursorUsageRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("CURSOR_USAGE", _rule.Id);
        Assert.Equal("Penggunaan T-SQL CURSOR (baris-per-baris)", _rule.Title);
        Assert.Equal(RiskLevel.Warning, _rule.Level);
    }

    [Fact]
    public void Evaluate_DefaultNamedCursorWithoutOptions_MentionsMissingOptimizationOptions()
    {
        var diag = Assert.Single(_rule.Evaluate("DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders;"));

        Assert.Equal("CURSOR_USAGE", diag.RuleId);
        Assert.Equal("Penggunaan T-SQL CURSOR (baris-per-baris)", diag.Title);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("'c'", diag.Message, StringComparison.Ordinal);
        Assert.Contains("LOCAL", diag.Message, StringComparison.Ordinal);
        Assert.Contains("FAST_FORWARD/FORWARD_ONLY", diag.Message, StringComparison.Ordinal);
        Assert.Contains("READ_ONLY", diag.Message, StringComparison.Ordinal);
        Assert.Contains("set-based", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR FAST_FORWARD FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR GLOBAL FAST_FORWARD FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c INSENSITIVE CURSOR FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c SCROLL CURSOR FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL STATIC FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL STATIC READ_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL KEYSET FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL DYNAMIC FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY OPTIMISTIC FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY SCROLL_LOCKS FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY FOR SELECT Id FROM dbo.Orders FOR UPDATE;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY FOR SELECT Id FROM dbo.Orders FOR UPDATE OF Status;")]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM dbo.Orders FOR UPDATE;")]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD SCROLL FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY FOR SELECT Id FROM dbo.T1 UNION SELECT Id FROM dbo.T2 FOR UPDATE;")]
    public void Evaluate_DefaultAndUnoptimizedNamedCursors_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("CURSOR_USAGE", diag.RuleId);
        Assert.Equal("Penggunaan T-SQL CURSOR (baris-per-baris)", diag.Title);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.False(string.IsNullOrWhiteSpace(diag.Message));
        Assert.Contains("set-based", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LOCAL FAST_FORWARD", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR SCROLL FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR GLOBAL FAST_FORWARD FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT Id FROM dbo.Orders FOR READ ONLY;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FOR SELECT Id FROM dbo.Orders FOR UPDATE;")]
    public void Evaluate_UnoptimizedCursorVariables_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("CURSOR_USAGE", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("'@c'", diag.Message, StringComparison.Ordinal);
        Assert.Contains("set-based", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("FAST_FORWARD", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL TYPE_WARNING FAST_FORWARD FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL STATIC FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL STATIC FORWARD_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY FOR SELECT Id FROM dbo.Orders FOR READ ONLY;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY FOR SELECT Id FROM dbo.T1 UNION ALL SELECT Id FROM dbo.T2 FOR READ ONLY;")]
    [InlineData("DECLARE c CURSOR LOCAL FORWARD_ONLY FOR (SELECT Id FROM dbo.T1) FOR READ ONLY;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FAST_FORWARD FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR STATIC FORWARD_ONLY READ_ONLY FOR SELECT Id FROM dbo.Orders;")]
    [InlineData("DECLARE @c CURSOR; SET @c = CURSOR FORWARD_ONLY FOR SELECT Id FROM dbo.Orders FOR READ ONLY;")]
    public void Evaluate_LightweightLocalReadOnlyForwardOnlyCursors_FlagsInfo(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("CURSOR_USAGE", diag.RuleId);
        Assert.Equal("Penggunaan T-SQL CURSOR (baris-per-baris)", diag.Title);
        Assert.Equal(RiskLevel.Info, diag.Level);
        Assert.False(string.IsNullOrWhiteSpace(diag.Message));
        Assert.Contains("set-based", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_FullCursorLifecycle_EmitsSingleDiagnosticAtDeclaration()
    {
        const string sql =
            "DECLARE @id INT;\n" +
            "DECLARE cur_orders CURSOR LOCAL FAST_FORWARD FOR\n" +
            "    SELECT Id FROM dbo.Orders;\n" +
            "OPEN cur_orders;\n" +
            "FETCH NEXT FROM cur_orders INTO @id;\n" +
            "WHILE @@FETCH_STATUS = 0\n" +
            "BEGIN\n" +
            "    FETCH NEXT FROM cur_orders INTO @id;\n" +
            "END;\n" +
            "CLOSE cur_orders;\n" +
            "DEALLOCATE cur_orders;";

        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("CURSOR_USAGE", diag.RuleId);
        Assert.Equal(RiskLevel.Info, diag.Level);
        Assert.Equal(2, diag.Line);
        Assert.Equal(1, diag.Column);
        Assert.Contains("'cur_orders'", diag.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SELECT Id, CustomerId FROM dbo.Orders WHERE Status = 1;")]
    [InlineData("WITH cte AS (SELECT Id, ROW_NUMBER() OVER (ORDER BY Id) AS rn FROM dbo.Orders) SELECT Id FROM cte WHERE rn <= 10;")]
    [InlineData("-- DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders;\nSELECT Id FROM dbo.Orders;")]
    [InlineData("/* DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders; */\nSELECT Id FROM dbo.Orders;")]
    [InlineData("SELECT 'DECLARE c CURSOR FOR SELECT 1' AS txt;")]
    [InlineData("DECLARE @c CURSOR;")]
    [InlineData("CREATE PROCEDURE dbo.sp_GetCursor @c CURSOR VARYING OUTPUT AS BEGIN SELECT 1; END;")]
    [InlineData("OPEN c; FETCH NEXT FROM c INTO @id; CLOSE c; DEALLOCATE c;")]
    public void Evaluate_SetBasedQueriesCommentsStringsAndBareCursorDeclarations_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_MultipleAndNestedCursorsInProcedure_ReportsAccuratePositionsAndNames()
    {
        const string sql =
            "CREATE PROCEDURE dbo.sp_ProcessOrders\n" +
            "AS\n" +
            "BEGIN\n" +
            "    DECLARE outer_cur CURSOR LOCAL FAST_FORWARD FOR\n" +
            "        SELECT Id FROM dbo.Orders;\n" +
            "    DECLARE @inner_cur CURSOR;\n" +
            "    SET @inner_cur = CURSOR FOR\n" +
            "        SELECT Id FROM dbo.OrderItems;\n" +
            "END;";

        var diagnostics = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diagnostics.Count);

        Assert.Equal(RiskLevel.Info, diagnostics[0].Level);
        Assert.Equal(4, diagnostics[0].Line);
        Assert.Equal(5, diagnostics[0].Column);
        Assert.Contains("'outer_cur'", diagnostics[0].Message, StringComparison.Ordinal);

        Assert.Equal(RiskLevel.Warning, diagnostics[1].Level);
        Assert.Equal(7, diagnostics[1].Line);
        Assert.Equal(5, diagnostics[1].Column);
        Assert.Contains("'@inner_cur'", diagnostics[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_HandlesEmptyNullAndMalformedSqlGracefully()
    {
        Assert.Empty(_rule.Evaluate(string.Empty));
        Assert.Empty(_rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => _rule.Evaluate("DECLARE c CURSOR").ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void SqlAnalyzer_CombinesWithOtherRulesAndRespectsProfileAndOverrides()
    {
        const string sql = "DECLARE c CURSOR FOR SELECT * FROM dbo.Orders;";

        var defaultAnalyzer = new SqlAnalyzer(SqlAnalyzer.CreateDefaultRules());
        var defaultReport = defaultAnalyzer.Analyze(sql);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "CURSOR_USAGE" && d.Level == RiskLevel.Warning);
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
                    ["CURSOR_USAGE"] = RiskLevel.Critical,
                },
            });
        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.True(overriddenReport.HasCritical);
        Assert.Equal(RiskLevel.Critical, Assert.Single(overriddenReport.Diagnostics).Level);
    }
}
