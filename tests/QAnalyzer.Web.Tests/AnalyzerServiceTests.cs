using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class AnalyzerServiceTests
{
    private readonly AnalyzerService _service = new();

    [Fact]
    public void Analyze_SelectStar_ReturnsWarning()
    {
        var resp = _service.Analyze(new AnalyzeRequest { Sql = "SELECT * FROM Orders;" });

        Assert.NotNull(resp);
        Assert.True(resp.Total >= 1);
        var d = Assert.Single(resp.Diagnostics, x => x.RuleId == "SELECT_STAR");
        Assert.Equal("Warning", d.Level);
    }

    [Fact]
    public void Analyze_ReportingProfile_DowngradesNoLockToInfo()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT * FROM Orders WITH (NOLOCK);",
            Profile = "reporting"
        });

        var nolock = Assert.Single(resp.Diagnostics, x => x.RuleId == "NOLOCK");
        Assert.Equal("Info", nolock.Level);
    }

    [Fact]
    public void Analyze_MaxDopEnabled_AddsOptionAndReportsTransformedSql()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM Orders WHERE Qty > 10;",
            EnableMaxDopTransform = true
        });

        Assert.NotNull(resp.TransformedSql);
        Assert.Contains("MAXDOP 1", resp.TransformedSql);
    }

    [Fact]
    public void Analyze_CriticalSeverity_FlagsHasCritical()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "UPDATE Orders SET Status = 'X';"
        });

        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.Level == "Critical");
    }

    [Fact]
    public void Analyze_MaxDopEnabled_OnCteWithoutSemicolon_AppendsSingleOuterOption()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "WITH cte AS (SELECT Id FROM (SELECT Id FROM Orders) s) SELECT Id FROM cte",
            EnableMaxDopTransform = true
        });

        Assert.NotNull(resp.TransformedSql);
        Assert.EndsWith("OPTION (MAXDOP 1)", resp.TransformedSql);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(resp.TransformedSql, @"OPTION\s*\(\s*MAXDOP\s+1\s*\)"));
    }

    [Fact]
    public void Analyze_WithSqlAndExecutionPlan_CombinesStaticAndPlanDiagnostics()
    {
        const string planXml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple>
                      <QueryPlan>
                        <MissingIndexes>
                          <MissingIndexGroup Impact="88.5">
                            <MissingIndex Database="[Sales]" Schema="[dbo]" Table="[Orders]">
                              <ColumnGroup Usage="EQUALITY">
                                <Column Name="[CustomerId]" ColumnId="2" />
                              </ColumnGroup>
                            </MissingIndex>
                          </MissingIndexGroup>
                        </MissingIndexes>
                        <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
                          <TableScan>
                            <Object Schema="[dbo]" Table="[AuditLog]" />
                          </TableScan>
                        </RelOp>
                        <RelOp NodeId="2" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek">
                          <IndexScan Lookup="true" Ordered="true">
                            <Object Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" />
                          </IndexScan>
                        </RelOp>
                        <RelOp NodeId="3" PhysicalOp="Sort" LogicalOp="Sort">
                          <Warnings>
                            <SpillToTempDb SpillLevel="2" />
                          </Warnings>
                        </RelOp>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT * FROM dbo.Orders;",
            SqlPlanXml = planXml,
        });

        Assert.True(resp.HasExecutionPlan);
        Assert.Equal(4, resp.PlanDiagnosticCount);
        Assert.Equal(5, resp.Total);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "SELECT_STAR");
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleMissingIndex);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleKeyLookup);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleTableScan);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb);
    }

    [Fact]
    public void Analyze_PlanOnlyWithoutSql_ParsesExecutionPlanAndSkipsMaxDopTransform()
    {
        const string planXml = """
            <ShowPlanXML>
              <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
                <TableScan>
                  <Object Schema="[dbo]" Table="[Orders]" />
                </TableScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "",
            EnableMaxDopTransform = true,
            SqlPlanXml = planXml,
        });

        Assert.True(resp.HasExecutionPlan);
        Assert.Equal(1, resp.PlanDiagnosticCount);
        Assert.Equal(1, resp.Total);
        Assert.Null(resp.TransformedSql);
        var diag = Assert.Single(resp.Diagnostics);
        Assert.Equal(ExecutionPlanParser.RuleTableScan, diag.RuleId);
    }

    [Fact]
    public void Analyze_CleanExecutionPlan_SetsHasExecutionPlanTrueWithZeroPlanDiagnostics()
    {
        const string planXml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek">
                <IndexScan Ordered="true">
                  <Object Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Id]" />
                </IndexScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "",
            SqlPlanXml = planXml,
        });

        Assert.True(resp.HasExecutionPlan);
        Assert.Equal(0, resp.PlanDiagnosticCount);
        Assert.Equal(0, resp.Total);
        Assert.False(resp.HasCritical);
    }

    [Theory]
    [InlineData("<ShowPlanXML><broken>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><ShowPlanXML>&xxe;</ShowPlanXML>")]
    public void Analyze_MalformedOrDtdSqlPlanXml_ThrowsFormatException(string invalidXml)
    {
        Assert.Throws<FormatException>(() => _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders;",
            SqlPlanXml = invalidXml,
        }));
    }

    [Fact]
    public void Analyze_WithPlanWarnings_DetectsImplicitConvertNoJoinPredicateAndMissingStats()
    {
        const string planXml = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp NodeId="1" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
                <Warnings NoJoinPredicate="true">
                  <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(nvarchar(20),[dbo].[Orders].[Code],0)" />
                  <ColumnsWithNoStatistics>
                    <ColumnReference Schema="[dbo]" Table="[Orders]" Column="[Status]" />
                  </ColumnsWithNoStatistics>
                </Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "",
            SqlPlanXml = planXml,
        });

        Assert.True(resp.HasExecutionPlan);
        Assert.Equal(3, resp.PlanDiagnosticCount);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleImplicitConvert);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleNoJoinPredicate && d.Level == "Critical");
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleMissingStatistics);
    }

    [Fact]
    public void Analyze_NonShowPlanXmlRoot_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders;",
            SqlPlanXml = "<root><item /></root>",
        }));
    }

    [Fact]
    public void Analyze_OverDeepSqlPlanXml_ThrowsFormatException()
    {
        var depth = ExecutionPlanParser.MaxXmlDepth + 1;
        var overDeepXml = "<ShowPlanXML>"
            + string.Concat(Enumerable.Repeat("<Node>", depth - 1))
            + string.Concat(Enumerable.Repeat("</Node>", depth - 1))
            + "</ShowPlanXML>";

        Assert.Throws<FormatException>(() => _service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders;",
            SqlPlanXml = overDeepXml,
        }));
    }

    [Fact]
    public void Analyze_HighVolumePlanDiagnostics_CapsDiagnosticsAtParserLimit()
    {
        var scans = string.Concat(Enumerable.Range(1, ExecutionPlanParser.MaxDiagnostics + 25).Select(i =>
            $"""
            <RelOp NodeId="{i}" PhysicalOp="Table Scan" LogicalOp="Table Scan">
              <TableScan><Object Schema="[dbo]" Table="[T{i}]" /></TableScan>
            </RelOp>
            """));

        var planXml = $"""
            <ShowPlanXML>
              {scans}
              <RelOp NodeId="9001" PhysicalOp="Sort" LogicalOp="Sort">
                <Warnings><SpillToTempDb SpillLevel="1" /></Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "",
            SqlPlanXml = planXml,
        });

        Assert.True(resp.HasExecutionPlan);
        Assert.Equal(ExecutionPlanParser.MaxDiagnostics, resp.PlanDiagnosticCount);
        Assert.Equal(ExecutionPlanParser.MaxDiagnostics, resp.Total);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb && d.Level == "Critical");
    }

    [Fact]
    public void Analyze_OverDeepSql_ThrowsFormatException()
    {
        var overDeepSql = "SELECT "
            + new string('(', SqlAnalyzer.MaxNestingDepth + 1)
            + "1"
            + new string(')', SqlAnalyzer.MaxNestingDepth + 1)
            + ";";

        Assert.Throws<FormatException>(() => _service.Analyze(new AnalyzeRequest { Sql = overDeepSql }));
    }

    [Fact]
    public void Analyze_HighVolumeSqlDiagnostics_CapsAtSqlAnalyzerMaxDiagnosticsAndRetainsCritical()
    {
        var floodedSql = string.Concat(Enumerable.Repeat("SELECT * FROM dbo.Orders;\n", SqlAnalyzer.MaxDiagnostics + 50))
            + "DELETE FROM dbo.Orders;";

        var resp = _service.Analyze(new AnalyzeRequest { Sql = floodedSql });

        Assert.Equal(SqlAnalyzer.MaxDiagnostics, resp.Total);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE" && d.Level == "Critical");
    }

    [Fact]
    public void Analyze_ReportingProfile_PreservesCriticalNoLockOnWriteStatements()
    {
        var resp = _service.Analyze(new AnalyzeRequest
        {
            Sql = "UPDATE dbo.Orders WITH (NOLOCK) SET Status = 'X' WHERE Id = 1;",
            Profile = "reporting",
        });

        Assert.True(resp.HasCritical);
        var nolock = Assert.Single(resp.Diagnostics, d => d.RuleId == "NOLOCK");
        Assert.Equal("Critical", nolock.Level);
    }
}
