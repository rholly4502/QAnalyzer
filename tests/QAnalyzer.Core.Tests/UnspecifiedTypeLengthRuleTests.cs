using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class UnspecifiedTypeLengthRuleTests
{
    private readonly UnspecifiedTypeLengthRule _rule = new();

    [Fact]
    public void RuleMetadata_HasExpectedIdTitleAndDefaultLevel()
    {
        Assert.Equal("UNSPECIFIED_TYPE_LENGTH", _rule.Id);
        Assert.Equal("Tipe data tanpa panjang/presisi eksplisit", _rule.Title);
        Assert.Equal(RiskLevel.Warning, _rule.Level);
    }

    [Theory]
    [InlineData("DECLARE @name VARCHAR;", "VARCHAR")]
    [InlineData("DECLARE @name NVARCHAR = N'Jakarta';", "NVARCHAR")]
    [InlineData("DECLARE @bin VARBINARY;", "VARBINARY")]
    [InlineData("DECLARE @t TABLE (Code VARCHAR);", "VARCHAR")]
    [InlineData("ALTER TABLE dbo.T ADD Code NVARCHAR;", "NVARCHAR")]
    [InlineData("ALTER TABLE dbo.T ALTER COLUMN Code VARCHAR NOT NULL;", "VARCHAR")]
    [InlineData("CREATE PROCEDURE dbo.p @code VARCHAR AS SELECT @code;", "VARCHAR")]
    [InlineData("CREATE TYPE dbo.ShortCode FROM VARCHAR;", "VARCHAR")]
    [InlineData("CREATE TYPE dbo.CodeList AS TABLE (Code NVARCHAR);", "NVARCHAR")]
    [InlineData("SELECT Name FROM OPENJSON(@json) WITH (Name VARCHAR '$.name');", "VARCHAR")]
    [InlineData("CREATE PARTITION FUNCTION pfCode (VARCHAR) AS RANGE RIGHT FOR VALUES ('A');", "VARCHAR")]
    [InlineData("DECLARE @a CHAR VARYING;", "VARCHAR")]
    [InlineData("DECLARE @b CHARACTER VARYING;", "VARCHAR")]
    [InlineData("DECLARE @c NATIONAL CHARACTER VARYING;", "NVARCHAR")]
    [InlineData("DECLARE @d BINARY VARYING;", "VARBINARY")]
    public void Evaluate_VariableLengthDeclarationWithoutLength_FlagsCriticalWithMaxSuggestion(
        string sql,
        string expectedType)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("UNSPECIFIED_TYPE_LENGTH", diag.RuleId);
        Assert.Equal("Tipe data tanpa panjang/presisi eksplisit", diag.Title);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains(expectedType, diag.Message, StringComparison.Ordinal);
        Assert.Contains("1 ", diag.Message, StringComparison.Ordinal);
        Assert.Contains($"{expectedType}(MAX)", diag.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CreateTableAndFunctionMultipleUnspecifiedVarTypes_FlagsAllAsCritical()
    {
        const string tableSql = "CREATE TABLE dbo.T (Name VARCHAR, Title NVARCHAR NOT NULL, Payload VARBINARY);";
        var tableDiags = _rule.Evaluate(tableSql).ToList();

        Assert.Equal(3, tableDiags.Count);
        Assert.All(tableDiags, d =>
        {
            Assert.Equal("UNSPECIFIED_TYPE_LENGTH", d.RuleId);
            Assert.Equal(RiskLevel.Critical, d.Level);
            Assert.Contains("1 ", d.Message, StringComparison.Ordinal);
            Assert.Contains("(MAX)", d.Suggestion, StringComparison.Ordinal);
        });
        Assert.Contains("VARCHAR", tableDiags[0].Message, StringComparison.Ordinal);
        Assert.Contains("NVARCHAR", tableDiags[1].Message, StringComparison.Ordinal);
        Assert.Contains("VARBINARY", tableDiags[2].Message, StringComparison.Ordinal);

        const string funcSql = "CREATE FUNCTION dbo.f(@x NVARCHAR) RETURNS VARCHAR AS BEGIN RETURN @x; END;";
        var funcDiags = _rule.Evaluate(funcSql).ToList();

        Assert.Equal(2, funcDiags.Count);
        Assert.All(funcDiags, d => Assert.Equal(RiskLevel.Critical, d.Level));
        Assert.Contains("NVARCHAR", funcDiags[0].Message, StringComparison.Ordinal);
        Assert.Contains("VARCHAR", funcDiags[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DECLARE @c CHAR;", "CHAR")]
    [InlineData("DECLARE @nc NCHAR;", "NCHAR")]
    [InlineData("DECLARE @b BINARY;", "BINARY")]
    [InlineData("DECLARE @a CHARACTER;", "CHAR")]
    [InlineData("DECLARE @b NATIONAL CHAR;", "NCHAR")]
    public void Evaluate_FixedLengthDeclarationWithoutLength_FlagsWarningWithoutMaxSuggestion(
        string sql,
        string expectedType)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("UNSPECIFIED_TYPE_LENGTH", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains(expectedType, diag.Message, StringComparison.Ordinal);
        Assert.Contains("1 ", diag.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("(MAX)", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_CreateTableFixedLengthColumnsWithoutLength_FlagsAllAsWarningWithoutMax()
    {
        const string sql = "CREATE TABLE dbo.T (Flag CHAR, Tag NCHAR, Hash BINARY);";
        var diags = _rule.Evaluate(sql).ToList();

        Assert.Equal(3, diags.Count);
        Assert.All(diags, d =>
        {
            Assert.Equal("UNSPECIFIED_TYPE_LENGTH", d.RuleId);
            Assert.Equal(RiskLevel.Warning, d.Level);
            Assert.Contains("1 ", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("(MAX)", d.Suggestion, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("SELECT CAST(Col AS VARCHAR) FROM dbo.T;", "CAST", "VARCHAR", true)]
    [InlineData("SELECT TRY_CAST(Col AS NVARCHAR) FROM dbo.T;", "TRY_CAST", "NVARCHAR", true)]
    [InlineData("SELECT CONVERT(VARBINARY, Col) FROM dbo.T;", "CONVERT", "VARBINARY", true)]
    [InlineData("SELECT TRY_CONVERT(CHAR, Col) FROM dbo.T;", "TRY_CONVERT", "CHAR", false)]
    [InlineData("SELECT CAST(Col AS NCHAR) FROM dbo.T;", "CAST", "NCHAR", false)]
    [InlineData("SELECT CONVERT(BINARY, Col) FROM dbo.T;", "CONVERT", "BINARY", false)]
    public void Evaluate_ConversionWithoutLength_FlagsWarningWithDefault30(
        string sql,
        string expectedConversion,
        string expectedType,
        bool expectMaxSuggestion)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("UNSPECIFIED_TYPE_LENGTH", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains(expectedConversion, diag.Message, StringComparison.Ordinal);
        Assert.Contains(expectedType, diag.Message, StringComparison.Ordinal);
        Assert.Contains("30 ", diag.Message, StringComparison.Ordinal);

        if (expectMaxSuggestion)
        {
            Assert.Contains($"{expectedType}(MAX)", diag.Suggestion, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("(MAX)", diag.Suggestion, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("DECLARE @amt DECIMAL;", "DECIMAL")]
    [InlineData("DECLARE @rate NUMERIC;", "NUMERIC")]
    [InlineData("DECLARE @d DEC;", "DECIMAL")]
    [InlineData("SELECT CAST(Col AS DECIMAL) FROM dbo.T;", "DECIMAL")]
    [InlineData("SELECT CONVERT(NUMERIC, Col) FROM dbo.T;", "NUMERIC")]
    [InlineData("SELECT TRY_CAST(Col AS NUMERIC) FROM dbo.T;", "NUMERIC")]
    [InlineData("SELECT TRY_CONVERT(DECIMAL, Col) FROM dbo.T;", "DECIMAL")]
    [InlineData("SELECT PARSE('12.5' AS DECIMAL);", "DECIMAL")]
    [InlineData("SELECT TRY_PARSE('12.5' AS NUMERIC);", "NUMERIC")]
    public void Evaluate_DecimalOrNumericWithoutPrecisionScale_FlagsWarningWithDefault18Zero(
        string sql,
        string expectedType)
    {
        var diag = Assert.Single(_rule.Evaluate(sql));

        Assert.Equal("UNSPECIFIED_TYPE_LENGTH", diag.RuleId);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains($"{expectedType}(18, 0)", diag.Message, StringComparison.Ordinal);
        Assert.Contains($"{expectedType}(18, 2)", diag.Suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CreateTableDecimalNumericColumnsWithoutPrecision_FlagsWarning()
    {
        const string sql = "CREATE TABLE dbo.T (Price DECIMAL, Rate NUMERIC);";
        var diags = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diags.Count);
        Assert.All(diags, d => Assert.Equal(RiskLevel.Warning, d.Level));
        Assert.Contains("DECIMAL(18, 0)", diags[0].Message, StringComparison.Ordinal);
        Assert.Contains("NUMERIC(18, 0)", diags[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_MixedAndNestedContexts_TracksPerNodeWithoutBleed()
    {
        var declareWithCast = _rule.Evaluate("DECLARE @x VARCHAR = CAST(@y AS VARCHAR);").ToList();
        Assert.Equal(2, declareWithCast.Count);
        Assert.Equal(RiskLevel.Critical, declareWithCast[0].Level);
        Assert.Contains("1 ", declareWithCast[0].Message, StringComparison.Ordinal);
        Assert.Equal(RiskLevel.Warning, declareWithCast[1].Level);
        Assert.Contains("CAST", declareWithCast[1].Message, StringComparison.Ordinal);
        Assert.Contains("30 ", declareWithCast[1].Message, StringComparison.Ordinal);

        var nestedCast = _rule.Evaluate("SELECT CAST(CAST(Col AS VARCHAR) AS NVARCHAR) FROM dbo.T;").ToList();
        Assert.Equal(2, nestedCast.Count);
        Assert.All(nestedCast, d => Assert.Equal(RiskLevel.Warning, d.Level));
        Assert.Contains("VARCHAR", nestedCast[0].Message, StringComparison.Ordinal);
        Assert.Contains("NVARCHAR", nestedCast[1].Message, StringComparison.Ordinal);

        var nestedConvert = _rule.Evaluate("SELECT CONVERT(NVARCHAR, CONVERT(VARCHAR, Col)) FROM dbo.T;").ToList();
        Assert.Equal(2, nestedConvert.Count);
        Assert.All(nestedConvert, d => Assert.Equal(RiskLevel.Warning, d.Level));
        Assert.Contains("NVARCHAR", nestedConvert[0].Message, StringComparison.Ordinal);
        Assert.Contains("VARCHAR", nestedConvert[1].Message, StringComparison.Ordinal);

        var tableDefault = _rule.Evaluate("CREATE TABLE dbo.T (Code VARCHAR DEFAULT CAST(1 AS VARCHAR));").ToList();
        Assert.Equal(2, tableDefault.Count);
        Assert.Equal(RiskLevel.Critical, tableDefault[0].Level);
        Assert.Equal(RiskLevel.Warning, tableDefault[1].Level);
        Assert.Contains("CAST", tableDefault[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DECLARE @a VARCHAR(50), @b NVARCHAR(MAX), @c VARBINARY(MAX), @d CHAR(1), @e NCHAR(2), @f BINARY(16);")]
    [InlineData("DECLARE @p DECIMAL(18, 2), @q DECIMAL(18), @r NUMERIC(10, 4);")]
    [InlineData("SELECT CAST(Col AS VARCHAR(100)), CONVERT(NVARCHAR(MAX), Col), TRY_CAST(Col AS CHAR(5)), TRY_CONVERT(DECIMAL(12, 2), Col) FROM dbo.T;")]
    [InlineData("CREATE SEQUENCE dbo.Seq AS DECIMAL START WITH 1 INCREMENT BY 1;")]
    [InlineData("SELECT IDENTITY(DECIMAL, 1, 1) AS Id INTO #Temp FROM dbo.Orders;")]
    [InlineData("SELECT CHAR(13) + CHAR(10) + NCHAR(65);")]
    [InlineData("DECLARE @i INT, @bi BIGINT, @fl FLOAT, @dt DATETIME2, @t TIME, @dto DATETIMEOFFSET, @g UNIQUEIDENTIFIER, @x XML, @s SYSNAME;")]
    [InlineData("DECLARE @u dbo.CustomVarchar;")]
    [InlineData("-- DECLARE @x VARCHAR;\nSELECT 1;")]
    [InlineData("/* CAST(x AS NVARCHAR) */\nSELECT 1;")]
    [InlineData("SELECT 'DECLARE @x VARCHAR' AS txt;")]
    public void Evaluate_ExplicitLengthsNonAffectedTypesCommentsAndLiterals_ReturnsEmpty(string sql)
    {
        Assert.Empty(_rule.Evaluate(sql));
    }

    [Fact]
    public void Evaluate_ReportsAccurateOneBasedLineAndColumn()
    {
        const string sql =
            "SELECT\n" +
            "    CAST(Id AS VARCHAR),\n" +
            "    CONVERT(DECIMAL, Amount)\n" +
            "FROM dbo.Orders;";

        var diagnostics = _rule.Evaluate(sql).ToList();

        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(2, diagnostics[0].Line);
        Assert.Equal(16, diagnostics[0].Column);
        Assert.Equal(3, diagnostics[1].Line);
        Assert.Equal(13, diagnostics[1].Column);
    }

    [Fact]
    public void Evaluate_HandlesNullEmptyWhitespaceAndMalformedSqlGracefully()
    {
        Assert.Empty(_rule.Evaluate(string.Empty));
        Assert.Empty(_rule.Evaluate("   \r\n\t "));
        Assert.Throws<ArgumentNullException>(() => _rule.Evaluate(null!).ToList());

        var ex = Record.Exception(() => _rule.Evaluate("DECLARE @x VARCHAR(").ToList());
        Assert.Null(ex);
    }

    [Fact]
    public void SqlAnalyzer_CombinesWithOtherRulesAndRespectsProfileAndOverrides()
    {
        const string sql = """
            DECLARE @name VARCHAR;
            SELECT * FROM dbo.Orders WHERE CustomerName = CAST(@name AS NVARCHAR);
            """;

        var defaultAnalyzer = new SqlAnalyzer(SqlAnalyzer.CreateDefaultRules());
        var defaultReport = defaultAnalyzer.Analyze(sql);
        Assert.True(defaultReport.HasCritical);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "SELECT_STAR" && d.Level == RiskLevel.Warning);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH" && d.Level == RiskLevel.Critical);
        Assert.Contains(defaultReport.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH" && d.Level == RiskLevel.Warning);

        var reportingAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings { Profile = AnalysisProfile.Reporting });
        var reportingReport = reportingAnalyzer.Analyze(sql);
        Assert.True(reportingReport.HasCritical);
        Assert.Equal([RiskLevel.Critical, RiskLevel.Warning], reportingReport.Diagnostics.Select(d => d.Level).ToArray());

        var overriddenAnalyzer = new SqlAnalyzer(
            [_rule],
            new AnalyzerSettings
            {
                RuleSeverityOverrides = new Dictionary<string, RiskLevel>
                {
                    ["UNSPECIFIED_TYPE_LENGTH"] = RiskLevel.Info,
                },
            });
        var overriddenReport = overriddenAnalyzer.Analyze(sql);
        Assert.False(overriddenReport.HasCritical);
        Assert.All(overriddenReport.Diagnostics, d => Assert.Equal(RiskLevel.Info, d.Level));
    }
}
