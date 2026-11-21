using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using QAnalyzer.Core;

public sealed class MaxDopTransformerTests
{
    private static void AssertValidTSql(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        Assert.Empty(errors);
    }

    private static int CountOptionMaxDop(string sql) =>
        Regex.Matches(sql, @"\bOPTION\s*\(\s*MAXDOP\s+1\s*\)", RegexOptions.IgnoreCase).Count;

    [Fact]
    public void AddMaxDopOne_AppendsToSimpleSelect()
    {
        var outSql = MaxDopTransformer.AddMaxDopOne("SELECT a FROM t;");
        Assert.EndsWith("OPTION (MAXDOP 1);", outSql);
    }

    [Fact]
    public void AddMaxDopOne_DoesNotTouchExistingOption()
    {
        var original = "SELECT a FROM t OPTION (RECOMPILE);";
        var outSql = MaxDopTransformer.AddMaxDopOne(original);
        Assert.Equal(original, outSql);
    }

    [Fact]
    public void AddMaxDopOne_Empty_ReturnsEmpty()
    {
        Assert.Equal("", MaxDopTransformer.AddMaxDopOne(""));
        Assert.Equal("   ", MaxDopTransformer.AddMaxDopOne("   "));
    }

    [Fact]
    public void AddMaxDopOne_SelectWithoutSemicolon_AppendsOptionMaxDopOne()
    {
        var outSql = MaxDopTransformer.AddMaxDopOne("SELECT a FROM t");
        Assert.Equal("SELECT a FROM t OPTION (MAXDOP 1)", outSql);
        AssertValidTSql(outSql);
    }

    [Theory]
    [InlineData("SELECT s.a FROM (SELECT b AS a FROM dbo.T) s;")]
    [InlineData("SELECT a FROM dbo.T1 WHERE id IN (SELECT t2_id FROM dbo.T2);")]
    [InlineData("SELECT a, (SELECT MAX(x) FROM dbo.T2) AS max_x FROM dbo.T1;")]
    public void AddMaxDopOne_Subqueries_AppendsOnlyToOutermostSelect(string sql)
    {
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        Assert.Equal(1, CountOptionMaxDop(outSql));
        Assert.EndsWith("OPTION (MAXDOP 1);", outSql);
        AssertValidTSql(outSql);
    }

    [Fact]
    public void AddMaxDopOne_Cte_AppendsOnlyToOuterSelect()
    {
        const string sql = "WITH cte AS (SELECT id FROM dbo.Orders) SELECT id FROM cte;";
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        Assert.Equal("WITH cte AS (SELECT id FROM dbo.Orders) SELECT id FROM cte OPTION (MAXDOP 1);", outSql);
        Assert.Equal(1, CountOptionMaxDop(outSql));
        AssertValidTSql(outSql);
    }

    [Theory]
    [InlineData("SELECT a FROM dbo.T1 UNION SELECT a FROM dbo.T2;")]
    [InlineData("SELECT a FROM dbo.T1 UNION ALL SELECT a FROM dbo.T2;")]
    [InlineData("SELECT a FROM dbo.T1 INTERSECT SELECT a FROM dbo.T2;")]
    [InlineData("SELECT a FROM dbo.T1 EXCEPT SELECT a FROM dbo.T2;")]
    public void AddMaxDopOne_SetOperations_AppendsSingleOptionAtEnd(string sql)
    {
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        Assert.Equal(1, CountOptionMaxDop(outSql));
        Assert.EndsWith("OPTION (MAXDOP 1);", outSql);
        AssertValidTSql(outSql);
    }

    [Theory]
    [InlineData("SELECT a FROM dbo.T ORDER BY a OFFSET 0 ROWS FETCH NEXT 10 ROWS ONLY;")]
    [InlineData("SELECT a FROM dbo.T FOR XML PATH('');")]
    [InlineData("SELECT a FROM dbo.T FOR JSON PATH;")]
    public void AddMaxDopOne_OffsetFetchAndForClauses_AppendsAtVeryEnd(string sql)
    {
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        Assert.Equal(1, CountOptionMaxDop(outSql));
        Assert.EndsWith("OPTION (MAXDOP 1);", outSql);
        AssertValidTSql(outSql);
    }

    [Fact]
    public void AddMaxDopOne_MultipleStatementsAndGoBatches_TransformsOnlySelectsWithoutOption()
    {
        const string sql = """
            UPDATE dbo.T SET a = 1 WHERE id = 1;
            SELECT a FROM dbo.T1
            SELECT b FROM dbo.T2 OPTION (RECOMPILE);
            GO
            SELECT c FROM dbo.T3;
            """;

        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        AssertValidTSql(outSql);
        Assert.Equal(2, CountOptionMaxDop(outSql));
        Assert.Contains("UPDATE dbo.T SET a = 1 WHERE id = 1;", outSql);
        Assert.Contains("SELECT a FROM dbo.T1 OPTION (MAXDOP 1)", outSql);
        Assert.Contains("SELECT b FROM dbo.T2 OPTION (RECOMPILE);", outSql);
        Assert.Contains("SELECT c FROM dbo.T3 OPTION (MAXDOP 1);", outSql);
    }

    [Fact]
    public void AddMaxDopOne_CommentsAndStringLiterals_DoNotCorruptOrSuppress()
    {
        const string sql = """
            -- SELECT b FROM dbo.Ignored
            /* SELECT c FROM dbo.AlsoIgnored */
            SELECT 'SELECT b FROM u' AS txt FROM dbo.T1 -- OPTION (RECOMPILE)
            ;
            """;

        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        AssertValidTSql(outSql);
        Assert.Equal(1, CountOptionMaxDop(outSql));
        Assert.Contains("'SELECT b FROM u'", outSql);
        Assert.Contains("FROM dbo.T1 OPTION (MAXDOP 1) -- OPTION (RECOMPILE)", outSql);
    }

    [Theory]
    [InlineData("SELECT a FROM t -- note", "SELECT a FROM t OPTION (MAXDOP 1) -- note")]
    [InlineData("SELECT a FROM t -- note\n;", "SELECT a FROM t OPTION (MAXDOP 1) -- note\n;")]
    [InlineData("SELECT a FROM t /* block */;", "SELECT a FROM t OPTION (MAXDOP 1) /* block */;")]
    [InlineData("SELECT a FROM t;;", "SELECT a FROM t OPTION (MAXDOP 1);;")]
    public void AddMaxDopOne_TrailingCommentsAndSemicolons_InsertsBeforeTrailingTokens(string sql, string expected)
    {
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        Assert.Equal(expected, outSql);
        AssertValidTSql(outSql);
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.v AS SELECT a FROM dbo.T;")]
    [InlineData("ALTER VIEW dbo.v AS SELECT a FROM dbo.T;")]
    [InlineData("CREATE OR ALTER VIEW dbo.v AS SELECT a FROM dbo.T;")]
    [InlineData("CREATE FUNCTION dbo.fn() RETURNS TABLE AS RETURN (SELECT a FROM dbo.T);")]
    [InlineData("DECLARE c CURSOR FOR SELECT a FROM dbo.T FOR READ ONLY;")]
    [InlineData("INSERT INTO dbo.Dest (a) SELECT a FROM dbo.Src;")]
    public void AddMaxDopOne_DisallowedOrNonStandaloneSelectContexts_RemainUntouched(string sql)
    {
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        Assert.Equal(sql, outSql);
        AssertValidTSql(outSql);
    }

    [Theory]
    [InlineData("IF @x = 1 SELECT a FROM dbo.T;")]
    [InlineData("CREATE PROCEDURE dbo.p AS BEGIN SELECT a FROM dbo.T; END;")]
    public void AddMaxDopOne_ExecutableSelectInControlFlowOrProc_AppendsOptionMaxDopOne(string sql)
    {
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);

        Assert.Equal(1, CountOptionMaxDop(outSql));
        Assert.Contains("SELECT a FROM dbo.T OPTION (MAXDOP 1);", outSql);
        AssertValidTSql(outSql);
    }

    [Theory]
    [InlineData("SELECT a FROM dbo.T;")]
    [InlineData("WITH cte AS (SELECT id FROM dbo.Orders) SELECT id FROM cte")]
    [InlineData("SELECT a FROM dbo.T -- trailing comment")]
    public void AddMaxDopOne_IsIdempotent(string sql)
    {
        var once = MaxDopTransformer.AddMaxDopOne(sql);
        var twice = MaxDopTransformer.AddMaxDopOne(once);

        Assert.Equal(once, twice);
        AssertValidTSql(once);
    }

    [Theory]
    [InlineData("SELECT a FROM")]
    [InlineData("SELECT a FROM t OPTION (RECOMPILE")]
    public void AddMaxDopOne_MalformedSql_ReturnsOriginalUnchanged(string sql)
    {
        var outSql = MaxDopTransformer.AddMaxDopOne(sql);
        Assert.Equal(sql, outSql);
    }
}
