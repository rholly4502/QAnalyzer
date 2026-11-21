using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class DynamicSqlRuleTests
{
    private readonly DynamicSqlRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("DYNAMIC_SQL", _rule.Id);
        Assert.Equal("Penggunaan dynamic SQL berisiko", _rule.Title);
        Assert.Equal(RiskLevel.Warning, _rule.Level);
    }

    [Theory]
    [InlineData("EXEC('SELECT Id FROM dbo.Users WHERE Name = ''' + @name + '''');")]
    [InlineData("EXECUTE(N'SELECT Id FROM dbo.Orders WHERE Id = ' + @id);")]
    public void Evaluate_DirectConcatenationInExec_FlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal("Penggunaan dynamic SQL berisiko", diag.Title);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("sp_executesql", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(diag.Message));
    }

    [Theory]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE Id = ' + CAST(@id AS NVARCHAR(20)); EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = CONCAT('SELECT Id FROM dbo.Orders WHERE Id = ', @id); EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = CONCAT_WS(' ', 'SELECT Id FROM dbo.Orders WHERE Id =', @id); EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = FORMAT(@id, 'SELECT Id FROM dbo.Orders WHERE Id = 0'); EXEC(@sql);")]
    public void Evaluate_DynamicSqlBuiltWithCastConcatConcatWsOrFormat_FlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Theory]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + N''''; EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE 1 = 1'; SET @sql += N' AND Name = ''' + @name + N''''; EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Users WHERE Name IN ('; SELECT @sql = @sql + '''' + u.Name + ''',' FROM dbo.Users u; EXEC(@sql);")]
    [InlineData("DECLARE @where NVARCHAR(MAX) = N' WHERE Name = ''' + @name + N''''; DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Users' + @where; EXEC(@sql);")]
    public void Evaluate_TaintedVariableViaDeclareSetSelectOrPropagation_FlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Fact]
    public void Evaluate_IfElseWhereOneBranchTaintsAndElseResetsStatic_FlagsCritical()
    {
        const string sql = """
            DECLARE @sql NVARCHAR(MAX);
            IF @flag = 1
                SET @sql = N'SELECT Id FROM dbo.Users WHERE Name = ''' + @name + N'''';
            ELSE
                SET @sql = N'SELECT 1;';
            EXEC(@sql);
            """;

        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Fact]
    public void Evaluate_InsertExecWithConcatenatedSql_FlagsCritical()
    {
        const string sql = "INSERT INTO dbo.Temp (Id) EXEC('SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + '''');";

        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Theory]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + N''''; EXEC sp_executesql @sql;")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + N''''; EXEC dbo.sp_executesql @sql;")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE Status = @Status AND Name = ''' + @name + N''''; EXEC sys.sp_executesql @stmt = @sql, @params = N'@Status VARCHAR(20)', @Status = @status;")]
    public void Evaluate_TaintedVariablePassedToSpExecuteSql_FlagsCriticalEvenWithParams(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Theory]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.' + QUOTENAME(@tbl) + N' WHERE Name = ''' + @name + N''''; EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.' + QUOTENAME(@tbl) + N' WHERE Name = ''' + @name + N''''; EXEC sp_executesql @sql, N'@Id INT', @Id = @id;")]
    public void Evaluate_QuotenameMixedWithRawConcatenation_FlagsCritical(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Theory]
    [InlineData("EXEC(@sql);")]
    [InlineData("CREATE PROCEDURE dbo.sp_Run @sql NVARCHAR(MAX) = N'SELECT 1;' AS BEGIN EXEC(@sql); END;")]
    [InlineData("INSERT INTO dbo.Temp (Id) EXEC(@sql);")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.' + QUOTENAME(@tbl); EXEC(@sql);")]
    [InlineData("EXEC sp_executesql @sql;")]
    [InlineData("EXEC sp_executesql @sql, NULL;")]
    [InlineData("EXEC sp_executesql @sql, N'';")]
    [InlineData("DECLARE @sql NVARCHAR(MAX); SELECT @sql = QueryText FROM dbo.SavedQueries WHERE Id = 1; EXEC(@sql);")]
    public void Evaluate_UnparameterizedOrUnknownDynamicSql_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("sp_executesql", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("EXEC dbo.sp_GetOrders @CustomerId = @id;")]
    [InlineData("EXEC @rc = dbo.sp_Run 1;")]
    [InlineData("EXEC('CREATE SCHEMA Sales;');")]
    [InlineData("EXEC(N'SELECT 1;' + N' SELECT 2;');")]
    [InlineData("EXEC('SELECT Id FROM dbo.Orders WHERE Id = ?', @id) AT LinkedSrv;")]
    [InlineData("DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.' + QUOTENAME(@tbl) + N' WHERE Id = ?'; EXEC(@sql, @id) AT LinkedSrv;")]
    [InlineData("EXEC sp_executesql N'SELECT Id FROM dbo.Orders WHERE Id = @Id', N'@Id INT', @Id = @id;")]
    [InlineData("EXEC sp_executesql @sql, N'@Id INT', @Id = @id;")]
    [InlineData("EXEC sys.sp_executesql @stmt = @sql, @params = N'@Id INT', @Id = @id;")]
    [InlineData("EXEC sp_executesql N'SELECT Id FROM dbo.Orders;';")]
    [InlineData("-- EXEC(@sql);")]
    [InlineData("/* EXEC('SELECT ' + @x); */ SELECT 1;")]
    [InlineData("SELECT 'EXEC(@sql)' AS txt;")]
    public void Evaluate_SafeParameterizedStaticProceduresAndComments_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_DynamicSearchStaticFragmentsQuotenameAndAllowlistedCaseWithSpExecuteSql_ReturnsEmpty()
    {
        const string dynamicSearchSql = """
            DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.' + QUOTENAME(@tbl) + N' WHERE 1 = 1';
            IF @CustomerId IS NOT NULL
                SET @sql += CHAR(13) + CHAR(10) + N' AND CustomerId = @CustomerId';
            IF @Status IS NOT NULL
                SET @sql += N' AND Status = @Status';
            SET @sql += N' ORDER BY ' + CASE @sort WHEN 'date' THEN N'OrderDate' ELSE N'Id' END;
            EXEC sp_executesql @sql, N'@CustomerId INT, @Status VARCHAR(20)', @CustomerId = @CustomerId, @Status = @Status;
            """;

        Assert.Empty(_rule.Evaluate(dynamicSearchSql));
    }

    [Fact]
    public void Evaluate_GoBatchIsolation_DoesNotLeakTaintAcrossBatches()
    {
        const string sql = """
            DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + N'''';
            GO
            DECLARE @sql NVARCHAR(MAX) = N'SELECT 1;';
            EXEC(@sql);
            """;

        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_MultilineScript_ReportsAccurateOneBasedLineAndColumn()
    {
        const string sql = "SELECT 1;\n    EXEC(@sql);";

        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("DYNAMIC_SQL", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Equal(2, diag.Line);
        Assert.Equal(5, diag.Column);
    }

    [Fact]
    public void Evaluate_HandlesEmptyNullAndMalformedSqlGracefully()
    {
        Assert.Empty(_rule.Evaluate(string.Empty));
        Assert.Empty(_rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => _rule.Evaluate("EXEC(").ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void SqlAnalyzer_RespectsRuleSeverityOverridesAndKeepsLevelInReportingProfile()
    {
        const string concatSql = "EXEC('SELECT Id FROM dbo.Users WHERE Name = ''' + @name + '''');";
        const string unparamSql = "EXEC(@sql);";

        var reportingAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });

        var criticalReport = reportingAnalyzer.Analyze(concatSql);
        Assert.True(criticalReport.HasCritical);
        Assert.Equal(RiskLevel.Critical, Assert.Single(criticalReport.Diagnostics).Level);

        var warningReport = reportingAnalyzer.Analyze(unparamSql);
        Assert.False(warningReport.HasCritical);
        Assert.Equal(RiskLevel.Warning, Assert.Single(warningReport.Diagnostics).Level);

        var overriddenAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["DYNAMIC_SQL"] = RiskLevel.Info,
                },
            });

        var overriddenReport = overriddenAnalyzer.Analyze(concatSql);
        Assert.False(overriddenReport.HasCritical);
        Assert.Equal(RiskLevel.Info, Assert.Single(overriddenReport.Diagnostics).Level);
    }
}
