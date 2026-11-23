using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class InsertWithoutColumnListRuleTests
{
    private readonly InsertWithoutColumnListRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("INSERT_WITHOUT_COLUMNS", _rule.Id);
        Assert.Equal("INSERT tanpa daftar kolom eksplisit", _rule.Title);
        Assert.Equal(RiskLevel.Warning, _rule.Level);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.Orders VALUES (1, 'A', GETDATE());", "dbo.Orders")]
    [InlineData("INSERT dbo.Orders VALUES (1, 'A');", "dbo.Orders")]
    [InlineData("INSERT INTO Orders SELECT Id, Name FROM dbo.StagingOrders;", "Orders")]
    [InlineData("INSERT INTO dbo.Orders EXEC dbo.usp_GetPendingOrders;", "dbo.Orders")]
    [InlineData("INSERT INTO #TempOrders VALUES (1, 'A');", "#TempOrders")]
    [InlineData("DECLARE @Stage TABLE (Id INT, Name NVARCHAR(50)); INSERT INTO @Stage VALUES (1, N'A');", "@Stage")]
    [InlineData("INSERT TOP (10) INTO dbo.Orders SELECT Id, Name FROM dbo.StagingOrders ORDER BY Id;", "dbo.Orders")]
    [InlineData("WITH cte AS (SELECT Id, Name FROM dbo.StagingOrders) INSERT INTO dbo.Orders SELECT Id, Name FROM cte;", "dbo.Orders")]
    [InlineData("INSERT INTO dbo.Archive SELECT Id FROM (DELETE FROM dbo.Orders OUTPUT DELETED.Id WHERE Id = 1) AS d;", "dbo.Archive")]
    public void Evaluate_InsertWithoutColumnList_FlagsWarningWithTargetName(string sql, string expectedTarget)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("INSERT_WITHOUT_COLUMNS", diag.RuleId);
        Assert.Equal("INSERT tanpa daftar kolom eksplisit", diag.Title);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains(expectedTarget, diag.Message, StringComparison.Ordinal);
        Assert.Contains("urutan fisik kolom", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT INTO", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MERGE INTO dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN NOT MATCHED THEN INSERT VALUES (s.Id, s.Name);")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.Name = s.Name WHEN NOT MATCHED BY TARGET THEN INSERT VALUES (s.Id, s.Name);")]
    [InlineData("INSERT INTO dbo.Archive (Id) SELECT d.Id FROM (MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN NOT MATCHED THEN INSERT VALUES (s.Id, s.Name) OUTPUT INSERTED.Id) AS d;")]
    public void Evaluate_MergeInsertWithoutColumnList_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("INSERT_WITHOUT_COLUMNS", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("MERGE", diag.Message, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED THEN INSERT", diag.Suggestion, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Orders OUTPUT DELETED.Id, DELETED.Name INTO dbo.OrderArchive WHERE Id = 1;", "dbo.OrderArchive")]
    [InlineData("UPDATE dbo.Orders SET Status = 'DONE' OUTPUT INSERTED.Id INTO @UpdatedIds WHERE Id = 1;", "@UpdatedIds")]
    [InlineData("INSERT INTO dbo.Orders (Id, Name) OUTPUT INSERTED.Id, INSERTED.Name INTO #AuditOrders VALUES (1, 'A');", "#AuditOrders")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN NOT MATCHED THEN INSERT (Id, Name) VALUES (s.Id, s.Name) OUTPUT INSERTED.Id INTO dbo.MergeLog;", "dbo.MergeLog")]
    public void Evaluate_OutputIntoWithoutColumnList_FlagsWarningWithTargetName(string sql, string expectedTarget)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("INSERT_WITHOUT_COLUMNS", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains($"OUTPUT INTO {expectedTarget}", diag.Message, StringComparison.Ordinal);
        Assert.Contains("OUTPUT ... INTO", diag.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_MultipleStatementsMissingColumns_FlagsAllInSourceOrder()
    {
        const string sql = """
            INSERT INTO dbo.Orders
            OUTPUT INSERTED.Id INTO dbo.OrderAudit
            VALUES (1, 'A');
            MERGE dbo.Orders AS t
            USING dbo.Staging AS s ON t.Id = s.Id
            WHEN NOT MATCHED THEN INSERT VALUES (s.Id, s.Name);
            """;

        var diags = _rule.Evaluate(sql).ToList();

        Assert.Equal(3, diags.Count);
        Assert.Equal(1, diags[0].Line);
        Assert.Contains("INSERT ke dbo.Orders", diags[0].Message, StringComparison.Ordinal);
        Assert.Equal(2, diags[1].Line);
        Assert.Contains("OUTPUT INTO dbo.OrderAudit", diags[1].Message, StringComparison.Ordinal);
        Assert.Equal(6, diags[2].Line);
        Assert.Contains("MERGE", diags[2].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.Orders (Id, Name) VALUES (1, 'A');")]
    [InlineData("INSERT dbo.Orders (Id, Name) SELECT Id, Name FROM dbo.StagingOrders;")]
    [InlineData("INSERT INTO dbo.Orders (Id, Name) EXEC dbo.usp_GetPendingOrders;")]
    [InlineData("INSERT INTO #TempOrders (Id) VALUES (1);")]
    [InlineData("DECLARE @Stage TABLE (Id INT); INSERT INTO @Stage (Id) VALUES (1);")]
    [InlineData("INSERT INTO dbo.Orders DEFAULT VALUES;")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN NOT MATCHED THEN INSERT (Id, Name) VALUES (s.Id, s.Name);")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Staging AS s ON t.Id = s.Id WHEN NOT MATCHED THEN INSERT DEFAULT VALUES;")]
    [InlineData("DELETE FROM dbo.Orders OUTPUT DELETED.Id, DELETED.Name INTO dbo.OrderArchive (Id, Name) WHERE Id = 1;")]
    [InlineData("INSERT INTO dbo.Orders (Id, Name) OUTPUT INSERTED.Id INTO dbo.OrderAudit (OrderId) VALUES (1, 'A');")]
    [InlineData("INSERT INTO dbo.Orders (Id, Name) OUTPUT INSERTED.Id, INSERTED.Name VALUES (1, 'A');")]
    [InlineData("-- INSERT INTO dbo.Orders VALUES (1, 'A');\nSELECT Id FROM dbo.Orders;")]
    [InlineData("/* INSERT INTO dbo.Orders VALUES (1, 'A'); */\nSELECT Id FROM dbo.Orders;")]
    [InlineData("SELECT 'INSERT INTO dbo.Orders VALUES (1, ''A'')' AS QueryText;")]
    public void Evaluate_ExplicitColumnListsDefaultValuesOutputWithoutIntoCommentsAndLiterals_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_ReportsAccurateOneBasedLineAndColumn()
    {
        const string sql =
            "DECLARE @id INT = 1;\n" +
            "    INSERT INTO dbo.Orders VALUES (@id, 'A');";

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

        var ex = Record.Exception(() => _rule.Evaluate("INSERT INTO dbo.Orders").ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void SqlAnalyzer_CombinesWithOtherRulesAndRespectsProfileAndOverrides()
    {
        const string sql = "INSERT INTO dbo.Orders SELECT * FROM dbo.StagingOrders;";

        var defaultAnalyzer = new SqlAnalyzer(SqlAnalyzer.CreateDefaultRules());
        var defaultReport = defaultAnalyzer.Analyze(sql);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "SELECT_STAR" && d.Level == RiskLevel.Warning);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "INSERT_WITHOUT_COLUMNS" && d.Level == RiskLevel.Warning);

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
                    ["INSERT_WITHOUT_COLUMNS"] = RiskLevel.Critical,
                },
            });
        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.True(overriddenReport.HasCritical);
        var overriddenDiag = Assert.Single(overriddenReport.Diagnostics);
        Assert.Equal(RiskLevel.Critical, overriddenDiag.Level);
    }
}
