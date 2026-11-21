using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class AstSqlRulesTests
{
    private sealed class CustomKeywordRule : ISqlRule
    {
        public string Id => "CUSTOM_WAITFOR";
        public string Title => "Penggunaan WAITFOR terdeteksi";
        public RiskLevel Level => RiskLevel.Warning;

        public IEnumerable<Diagnostic> Evaluate(string sql)
        {
            if (sql.Contains("WAITFOR", StringComparison.OrdinalIgnoreCase))
            {
                yield return new Diagnostic(Level, Id, Title, "Query memakai WAITFOR.");
            }
        }
    }

    // --- SelectStarRule ---

    [Theory]
    [InlineData("-- SELECT * FROM dbo.Orders;")]
    [InlineData("/* SELECT * FROM dbo.Orders; */ SELECT Id FROM dbo.Orders;")]
    [InlineData("SELECT 'SELECT * FROM dbo.Orders' AS txt;")]
    public void SelectStarRule_IgnoresCommentsAndStringLiterals(string sql)
    {
        var rule = new SelectStarRule();
        Assert.Empty(rule.Evaluate(sql));
    }

    [Theory]
    [InlineData("SELECT o.* FROM dbo.Orders o;")]
    [InlineData("SELECT TOP (10) * FROM dbo.Orders;")]
    [InlineData("SELECT DISTINCT * FROM dbo.Orders;")]
    [InlineData("SELECT Id, * FROM dbo.Orders;")]
    [InlineData("WITH cte AS (SELECT * FROM dbo.Orders) SELECT Id FROM cte;")]
    public void SelectStarRule_FlagsQualifiedTopDistinctMixedAndCteStars(string sql)
    {
        var rule = new SelectStarRule();
        var diags = rule.Evaluate(sql).ToList();

        var diag = Assert.Single(diags);
        Assert.Equal("SELECT_STAR", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
    }

    [Fact]
    public void SelectStarRule_DoesNotFlagCountStarOrExistsSubqueryStar()
    {
        const string sql = """
            SELECT COUNT(*) AS Total,
                   c.Id
            FROM dbo.Customers c
            WHERE EXISTS (SELECT * FROM dbo.Orders o WHERE o.CustomerId = c.Id);
            """;

        var rule = new SelectStarRule();
        Assert.Empty(rule.Evaluate(sql));
    }

    [Fact]
    public void SelectStarRule_ReportsAccurateOneBasedLineAndColumn()
    {
        const string sql = "SELECT Id\nFROM dbo.Users;\nSELECT\n    o.*\nFROM dbo.Orders o;";

        var rule = new SelectStarRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal(4, diag.Line);
        Assert.Equal(5, diag.Column);
    }

    [Fact]
    public void SelectStarRule_HandlesEmptyNullAndMalformedSqlGracefully()
    {
        var rule = new SelectStarRule();

        Assert.Empty(rule.Evaluate(""));
        Assert.Empty(rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => rule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => rule.Evaluate("SELECT * FROM").ToList());
        Assert.Null(ex);
    }

    // --- NoLockRule ---

    [Theory]
    [InlineData("-- SELECT Id FROM dbo.T WITH (NOLOCK);")]
    [InlineData("/* WITH (NOLOCK) */ SELECT Id FROM dbo.T;")]
    [InlineData("SELECT 'WITH (NOLOCK)' AS hint FROM dbo.T;")]
    public void NoLockRule_IgnoresCommentsAndStringLiterals(string sql)
    {
        var rule = new NoLockRule();
        Assert.Empty(rule.Evaluate(sql));
    }

    [Fact]
    public void NoLockRule_IsolatesPreviousUpdateStatementFromSubsequentSelect()
    {
        const string sql = """
            UPDATE dbo.A SET x = 1 WHERE id = 1;
            SELECT id FROM dbo.B WITH (NOLOCK);
            """;

        var rule = new NoLockRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal("NOLOCK", diag.RuleId);
        Assert.Equal(RiskLevel.Info, diag.Level);
    }

    [Fact]
    public void NoLockRule_IsolatesPreviousSelectStarFromSubsequentPlainSelect()
    {
        const string sql = """
            SELECT * FROM dbo.A;
            SELECT id FROM dbo.B WITH (NOLOCK);
            """;

        var rule = new NoLockRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal("NOLOCK", diag.RuleId);
        Assert.Equal(RiskLevel.Info, diag.Level);
    }

    [Fact]
    public void NoLockRule_WithQualifiedSelectStar_IsWarning()
    {
        var rule = new NoLockRule();
        var diag = Assert.Single(rule.Evaluate("SELECT t.* FROM dbo.T t WITH (NOLOCK);"));

        Assert.Equal(RiskLevel.Warning, diag.Level);
    }

    [Theory]
    [InlineData("SELECT id FROM dbo.T (NOLOCK);")]
    [InlineData("SELECT id FROM dbo.T WITH (READUNCOMMITTED);")]
    public void NoLockRule_SupportsBareParenHintAndReadUncommittedAsInfo(string sql)
    {
        var rule = new NoLockRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal(RiskLevel.Info, diag.Level);
    }

    [Theory]
    [InlineData("DELETE t FROM dbo.T t WITH (NOLOCK) WHERE t.id = 1;")]
    [InlineData("INSERT INTO dbo.Dest (id) SELECT id FROM dbo.Src WITH (NOLOCK);")]
    [InlineData("MERGE dbo.T AS target USING dbo.S AS src WITH (NOLOCK) ON target.id = src.id WHEN MATCHED THEN UPDATE SET target.v = src.v;")]
    [InlineData("WITH cte AS (SELECT id FROM dbo.S WITH (NOLOCK)) UPDATE dbo.T SET a = 1 FROM dbo.T JOIN cte ON dbo.T.id = cte.id;")]
    public void NoLockRule_InWriteStatementsAndUpdateCte_IsCritical(string sql)
    {
        var rule = new NoLockRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal(RiskLevel.Critical, diag.Level);
    }

    [Fact]
    public void NoLockRule_HandlesGoBatchSeparatorCleanly()
    {
        const string sql = """
            UPDATE dbo.A SET x = 1 WHERE id = 1
            GO
            SELECT id FROM dbo.B WITH (NOLOCK);
            """;

        var rule = new NoLockRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal(RiskLevel.Info, diag.Level);
    }

    [Fact]
    public void NoLockRule_HandlesNullAndMalformedSqlGracefully()
    {
        var rule = new NoLockRule();

        Assert.Empty(rule.Evaluate("   "));
        Assert.Throws<ArgumentNullException>(() => rule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => rule.Evaluate("SELECT FROM WITH (NOLOCK)").ToList());
        Assert.Null(ex);
    }

    // --- UpdateWithoutWhereRule ---

    [Theory]
    [InlineData("-- UPDATE dbo.Customers SET active = 0;")]
    [InlineData("/* UPDATE dbo.Customers SET active = 0; */ SELECT 1;")]
    [InlineData("SELECT 'UPDATE dbo.Customers SET active = 0' AS msg;")]
    public void UpdateWithoutWhereRule_IgnoresCommentsAndStringLiterals(string sql)
    {
        var rule = new UpdateWithoutWhereRule();
        Assert.Empty(rule.Evaluate(sql));
    }

    [Fact]
    public void UpdateWithoutWhereRule_FlagsOnlyStatementMissingWhereInMultiStatementScript()
    {
        const string sql = """
            UPDATE dbo.T1 SET a = 1;
            UPDATE dbo.T2 SET b = 2 WHERE id = 1;
            """;

        var rule = new UpdateWithoutWhereRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal("UPDATE_NO_WHERE", diag.RuleId);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Equal(1, diag.Line);
    }

    [Theory]
    [InlineData("UPDATE dbo.T SET a = (SELECT TOP 1 b FROM dbo.S) WHERE id = @id;")]
    [InlineData("UPDATE t SET t.a = 1 FROM dbo.T t WHERE t.id = 1;")]
    [InlineData("UPDATE dbo.T SET a = 1 WHERE CURRENT OF cur;")]
    public void UpdateWithoutWhereRule_SkipsUpdatesWithWhereClauseIncludingSubqueryInSetAndCurrentOf(string sql)
    {
        var rule = new UpdateWithoutWhereRule();
        Assert.Empty(rule.Evaluate(sql));
    }

    [Fact]
    public void UpdateWithoutWhereRule_FlagsAcrossGoBatchSeparator()
    {
        const string sql = """
            UPDATE dbo.T1 SET a = 1
            GO
            SELECT 1 WHERE 1 = 1;
            """;

        var rule = new UpdateWithoutWhereRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Equal(1, diag.Line);
    }

    [Fact]
    public void UpdateWithoutWhereRule_FlagsCteUpdateWithoutWhereAtUpdateLine()
    {
        const string sql = """
            WITH cte AS (SELECT id, active FROM dbo.T)
            UPDATE cte SET active = 0;
            """;

        var rule = new UpdateWithoutWhereRule();
        var diag = Assert.Single(rule.Evaluate(sql));

        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Equal(2, diag.Line);
    }

    [Fact]
    public void UpdateWithoutWhereRule_HandlesNullAndMalformedSqlGracefully()
    {
        var rule = new UpdateWithoutWhereRule();

        Assert.Empty(rule.Evaluate("   "));
        Assert.Throws<ArgumentNullException>(() => rule.Evaluate(null!).ToList());
        var ex = Record.Exception(() => rule.Evaluate("UPDATE dbo.T SET").ToList());
        Assert.Null(ex);
    }

    // --- SqlAnalyzer Integration ---

    [Fact]
    public void SqlAnalyzer_CombinesAstAndNonAstRulesWithReportingProfileAndOverrides()
    {
        const string sql = """
            SELECT * FROM dbo.Orders;
            SELECT id FROM dbo.Users WITH (NOLOCK);
            UPDATE dbo.Audit SET synced = 1;
            WAITFOR DELAY '00:00:01';
            """;

        var settings = new AnalyzerSettings
        {
            Profile = AnalysisProfile.Reporting,
            RuleSeverityOverrides = new Dictionary<string, RiskLevel>
            {
                ["CUSTOM_WAITFOR"] = RiskLevel.Info,
            },
        };

        var analyzer = new SqlAnalyzer(
        [
            new SelectStarRule(),
            new NoLockRule(),
            new UpdateWithoutWhereRule(),
            new CustomKeywordRule(),
        ], settings);

        var report = analyzer.Analyze(sql);

        Assert.Equal(4, report.Total);
        Assert.True(report.HasCritical);
        Assert.Equal(RiskLevel.Info, Assert.Single(report.Diagnostics, d => d.RuleId == "SELECT_STAR").Level);
        Assert.Equal(RiskLevel.Info, Assert.Single(report.Diagnostics, d => d.RuleId == "NOLOCK").Level);
        Assert.Equal(RiskLevel.Critical, Assert.Single(report.Diagnostics, d => d.RuleId == "UPDATE_NO_WHERE").Level);
        Assert.Equal(RiskLevel.Info, Assert.Single(report.Diagnostics, d => d.RuleId == "CUSTOM_WAITFOR").Level);
    }
}
