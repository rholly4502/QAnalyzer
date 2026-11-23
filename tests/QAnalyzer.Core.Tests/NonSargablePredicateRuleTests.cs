using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class NonSargablePredicateRuleTests
{
    private readonly NonSargablePredicateRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("NON_SARGABLE", _rule.Id);
        Assert.Equal("Predicate non-SARGable", _rule.Title);
        Assert.Equal(RiskLevel.Warning, _rule.Level);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '%john%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '%john';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '_abc';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE N'%abc';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name NOT LIKE '%abc';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '%' + @q + '%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE CONCAT('%', @q);")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE ('%' + @q);")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '' + '%abc';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE CONCAT('', '%', @q);")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE CAST('%abc' AS NVARCHAR(50));")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '[A-Z]ohn%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '[ab]c%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Code LIKE '[^0-9]%';")]
    public void Evaluate_LeadingWildcardLike_FlagsWarning(string sql)
    {
        var diags = _rule.Evaluate(sql).ToList();

        var diag = Assert.Single(diags);
        Assert.Equal("NON_SARGABLE", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("LIKE", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(diag.Suggestion));
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE 'john%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE 'john';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE @pattern;")]
    [InlineData("SELECT Id FROM dbo.Users WHERE 'hello' LIKE '%ell%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '%foo' ESCAPE '%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '\\[abc%' ESCAPE '\\';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '[%]promo%';")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Name LIKE '[_]temp%';")]
    public void Evaluate_SargableOrEscapedLike_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_FunctionOnColumnAndReversedComparison_ReportsAccurateLineAndColumn()
    {
        const string sql = "SELECT Id\nFROM dbo.Orders\nWHERE YEAR(OrderDate) = 2025;";

        var diag = Assert.Single(_rule.Evaluate(sql));
        Assert.Equal("NON_SARGABLE", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Equal(3, diag.Line);
        Assert.Equal(7, diag.Column);

        const string reversedSql = "SELECT Id\nFROM dbo.Orders\nWHERE 2025 = YEAR(OrderDate);";
        var reversedDiag = Assert.Single(_rule.Evaluate(reversedSql));
        Assert.Equal(3, reversedDiag.Line);
        Assert.Equal(14, reversedDiag.Column);
    }

    [Fact]
    public void Evaluate_FunctionOnColumnComparedToFunctionOnParameter_FlagsOnlyColumnSide()
    {
        var diags = _rule.Evaluate("SELECT Id FROM dbo.Users WHERE LOWER(Email) = LOWER(@email);").ToList();

        var diag = Assert.Single(diags);
        Assert.Equal(32, diag.Column);
    }

    [Fact]
    public void Evaluate_NestedFunctionsOnColumn_EmitsSingleDiagnostic()
    {
        var diags = _rule.Evaluate("SELECT Id FROM dbo.Users WHERE UPPER(LTRIM(RTRIM(Name))) = 'JOHN';").ToList();

        var diag = Assert.Single(diags);
        Assert.Equal("NON_SARGABLE", diag.RuleId);
        Assert.Equal(32, diag.Column);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE ISNULL(DeletedAt, '1900-01-01') = '1900-01-01';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE COALESCE(Status, 'Open') = 'Open';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CustomerId = ISNULL(@CustomerId, CustomerId);")]
    public void Evaluate_NullCoalescingOnColumn_FlagsWithIsNullCoalesceSuggestion(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NON_SARGABLE", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("ISNULL/COALESCE", diag.Message, StringComparison.Ordinal);
        Assert.Contains("IS NULL", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CAST(OrderId AS VARCHAR(20)) = @id;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CONVERT(VARCHAR(10), CreatedAt, 120) = '2025-01-01';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE TRY_CAST(Code AS INT) = 1;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE LEFT(Code, 3) = 'ABC';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE RIGHT(Code, 3) = 'XYZ';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE TRIM(Code) = 'ABC';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE TRIM(' ' FROM Code) = 'ABC';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DATEDIFF(DAY, OrderDate, GETDATE()) > 7;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CreatedAt AT TIME ZONE 'UTC' = @dt;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CASE WHEN Status = 'A' THEN 1 ELSE 0 END = 1;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE LOWER(Status) IN ('open', 'pending');")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE YEAR(OrderDate) BETWEEN 2023 AND 2025;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE NULLIF(Code, '') IS NULL;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE LOWER(Name) LIKE 'abc%';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE { fn UCASE(Code) } = 'ABC';")]
    [InlineData("MERGE dbo.T t USING dbo.S s ON t.Id = s.Id WHEN MATCHED AND YEAR(t.UpdatedAt) = 2025 THEN DELETE;")]
    public void Evaluate_FunctionsConversionsAndPredicatesOnColumn_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NON_SARGABLE", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
    }

    [Fact]
    public void Evaluate_InPredicateWithFunctionsOnValuesWhenLeftIsParameter_FlagsValues()
    {
        var diags = _rule.Evaluate("SELECT Id FROM dbo.Users WHERE @code IN (LOWER(PrimaryCode), TRIM(SecondaryCode));").ToList();

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal("NON_SARGABLE", d.RuleId));
    }

    [Fact]
    public void Evaluate_BetweenWithFunctionsOnBoundsWhenLeftIsLiteral_FlagsBounds()
    {
        var diags = _rule.Evaluate("SELECT Id FROM dbo.Orders WHERE 2025 BETWEEN YEAR(StartDate) AND YEAR(EndDate);").ToList();

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal("NON_SARGABLE", d.RuleId));
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Price * 1.1 > 100;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Quantity + 1 = @target;")]
    [InlineData("SELECT Id FROM dbo.Users WHERE FirstName + ' ' + LastName = 'John Doe';")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE -Amount > 100;")]
    [InlineData("SELECT a.Id FROM dbo.Orders a JOIN dbo.Invoices b ON a.OrderId + 1 = b.OrderId;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Price * 1.1 > MinPrice;")]
    public void Evaluate_ArithmeticOrUnaryOnColumn_FlagsWarning(string sql)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("NON_SARGABLE", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("aritmatika", diag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_ColumnComparedToSumOfColumns_DoesNotFlagArithmetic()
    {
        Assert.Empty(_rule.Evaluate("SELECT a.Id FROM dbo.Orders a WHERE a.Total = a.SubTotal + a.Tax;"));
    }

    [Fact]
    public void Evaluate_JoinOnBothSidesWrapped_FlagsBothOperands()
    {
        const string sql = "SELECT a.Id FROM dbo.A a JOIN dbo.B b ON LOWER(a.Email) = LOWER(b.Email);";

        var diags = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal("NON_SARGABLE", d.RuleId));
    }

    [Fact]
    public void Evaluate_MergeUpdateDeleteAndExistsSubquery_FlagsPredicateViolations()
    {
        const string mergeSql = "MERGE dbo.T t USING dbo.S s ON CAST(t.Id AS VARCHAR(20)) = s.Code WHEN MATCHED THEN DELETE;";
        Assert.Single(_rule.Evaluate(mergeSql));

        const string updateSql = "UPDATE dbo.Orders SET Status = 'Closed' WHERE YEAR(CreatedAt) = 2024;";
        Assert.Single(_rule.Evaluate(updateSql));

        const string deleteSql = "DELETE FROM dbo.Orders WHERE Name LIKE '%test%';";
        Assert.Single(_rule.Evaluate(deleteSql));

        const string existsSql = "SELECT Id FROM dbo.Orders o WHERE EXISTS (SELECT 1 FROM dbo.Items i WHERE LOWER(i.Code) = 'abc');";
        Assert.Single(_rule.Evaluate(existsSql));
    }

    [Fact]
    public void Evaluate_SubquerySelectListAndScalarSubqueryInsideIsNull_AreNotFlagged()
    {
        const string inSubquery = "SELECT Id FROM dbo.Orders WHERE CustomerId IN (SELECT UPPER(Id) FROM dbo.Customers WHERE Status = 'Active');";
        Assert.Empty(_rule.Evaluate(inSubquery));

        const string scalarIsNull = "SELECT Id FROM dbo.Orders WHERE Amount > ISNULL((SELECT MAX(x.Val) FROM dbo.X x WHERE x.Active = 1), 0);";
        Assert.Empty(_rule.Evaluate(scalarIsNull));
    }

    [Theory]
    [InlineData("SELECT Id FROM dbo.Orders WHERE OrderDate >= DATEADD(DAY, -7, GETDATE());")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE CreatedAt >= DATEADD(MONTH, -1, CURRENT_TIMESTAMP);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE DATEDIFF(DAY, @start, @end) > 7;")]
    [InlineData("SELECT Id FROM dbo.Users WHERE Email = LOWER(@email) AND CreatedAt = CAST(@dt AS DATETIME);")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE Status = CASE WHEN @flag = 1 THEN 'A' ELSE 'B' END;")]
    [InlineData("SELECT Id FROM dbo.Orders WHERE OrderDate BETWEEN StartDate AND DATEADD(DAY, 7, StartDate);")]
    [InlineData("SELECT YEAR(OrderDate), LOWER(Name), Price * 1.1 FROM dbo.Orders WHERE Id = @id GROUP BY YEAR(OrderDate), LOWER(Name), Price * 1.1 HAVING COUNT(*) > 1 ORDER BY LOWER(Name);")]
    [InlineData("UPDATE dbo.Orders SET Price = Price * 1.1 WHERE Id = @id;")]
    [InlineData("-- WHERE YEAR(OrderDate) = 2025")]
    [InlineData("/* WHERE Name LIKE '%a%' */ SELECT 1;")]
    [InlineData("SELECT 'WHERE YEAR(OrderDate) = 2025' AS txt;")]
    public void Evaluate_SargableNonPredicateAndLiteralQueries_ReturnEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_HandlesEmptyNullMalformedSqlAndGoBatchesGracefully()
    {
        Assert.Empty(_rule.Evaluate(string.Empty));
        Assert.Empty(_rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());

        var ex = Record.Exception(() => _rule.Evaluate("SELECT Id FROM dbo.Orders WHERE YEAR(").ToList());
        Assert.Null(ex);

        const string goSql = """
            SELECT Id FROM dbo.Orders WHERE Id = @id
            GO
            SELECT Id FROM dbo.Users WHERE LOWER(Email) = 'a@b.com';
            """;
        var diag = Assert.Single(_rule.Evaluate(goSql));
        Assert.Equal(3, diag.Line);
    }

    [Fact]
    public void SqlAnalyzer_RespectsRuleSeverityOverridesAndKeepsWarningInReportingProfile()
    {
        const string sql = "SELECT Id FROM dbo.Orders WHERE YEAR(OrderDate) = 2025;";

        var reportingAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });
        var reportingReport = reportingAnalyzer.Analyze(sql);
        var reportingDiag = Assert.Single(reportingReport.Diagnostics);
        Assert.Equal(RiskLevel.Warning, reportingDiag.Level);

        var criticalAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["NON_SARGABLE"] = RiskLevel.Critical,
                },
            });
        var criticalReport = criticalAnalyzer.Analyze(sql);
        Assert.True(criticalReport.HasCritical);
        Assert.Equal(RiskLevel.Critical, Assert.Single(criticalReport.Diagnostics).Level);

        var infoAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["NON_SARGABLE"] = RiskLevel.Info,
                },
            });
        var infoReport = infoAnalyzer.Analyze(sql);
        Assert.Equal(RiskLevel.Info, Assert.Single(infoReport.Diagnostics).Level);
    }
}
