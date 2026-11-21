using QAnalyzer.Core;

public sealed class ExecutionPlanParserTests
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private readonly ExecutionPlanParser _parser = new();

    [Fact]
    public void Parse_NullXml_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _parser.Parse(null!));
    }

    [Fact]
    public void Parse_WhitespaceXml_ReturnsEmpty()
    {
        var result = _parser.Parse("   \r\n\t  ");

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_MalformedXml_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => _parser.Parse("<ShowPlanXML><RelOp></ShowPlanXML>"));
    }

    [Fact]
    public void Parse_DtdEntityXml_ThrowsFormatException()
    {
        const string xxeXml = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [ <!ENTITY xxe SYSTEM "file:///etc/passwd"> ]>
            <ShowPlanXML>&xxe;</ShowPlanXML>
            """;

        Assert.Throws<FormatException>(() => _parser.Parse(xxeXml));
    }

    [Fact]
    public void Parse_MissingIndex_ReturnsWarningWithCreateIndexSuggestion()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple>
                      <QueryPlan>
                        <MissingIndexes>
                          <MissingIndexGroup Impact="87.5">
                            <MissingIndex Database="[SalesDb]" Schema="[dbo]" Table="[Orders]">
                              <ColumnGroup Usage="EQUALITY">
                                <Column Name="[CustomerId]" ColumnId="2" />
                              </ColumnGroup>
                              <ColumnGroup Usage="INEQUALITY">
                                <Column Name="[OrderDate]" ColumnId="3" />
                              </ColumnGroup>
                              <ColumnGroup Usage="INCLUDE">
                                <Column Name="[TotalAmount]" ColumnId="4" />
                              </ColumnGroup>
                            </MissingIndex>
                          </MissingIndexGroup>
                        </MissingIndexes>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleMissingIndex);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("dbo.Orders", diag.Message);
        Assert.Contains("87.5%", diag.Message);
        Assert.NotNull(diag.Suggestion);
        Assert.Contains("CREATE NONCLUSTERED INDEX", diag.Suggestion);
        Assert.Contains("CustomerId, OrderDate", diag.Suggestion);
        Assert.Contains("INCLUDE (TotalAmount)", diag.Suggestion);
        Assert.True(diag.Line > 0);
    }

    [Fact]
    public void Parse_KeyLookupViaIndexScanLookupAttribute_ReturnsWarning()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="3" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek">
                <IndexScan Lookup="true" Ordered="true">
                  <Object Database="[SalesDb]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" />
                </IndexScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleKeyLookup);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("dbo.Orders", diag.Message);
        Assert.Contains("PK_Orders", diag.Message);
        Assert.Contains("INCLUDE", diag.Suggestion);
    }

    [Fact]
    public void Parse_RidLookupPhysicalOp_ReturnsWarning()
    {
        const string xml = """
            <ShowPlanXML>
              <RelOp NodeId="2" PhysicalOp="RID Lookup" LogicalOp="RID Lookup">
                <IndexScan Ordered="false">
                  <Object Schema="[dbo]" Table="[StagingHeap]" />
                </IndexScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleKeyLookup);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("dbo.StagingHeap", diag.Message);
    }

    [Fact]
    public void Parse_TableScan_ReturnsWarningWithTableName()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
                <TableScan>
                  <Object Database="[SalesDb]" Schema="[dbo]" Table="[AuditLog]" />
                </TableScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleTableScan);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("dbo.AuditLog", diag.Message);
        Assert.True(diag.Line > 0);
    }

    [Fact]
    public void Parse_SpillToTempDb_ReturnsCriticalWithOperatorAndLevel()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="4" PhysicalOp="Sort" LogicalOp="Sort">
                <Warnings>
                  <SpillToTempDb SpillLevel="2" SpilledThreadCount="1" />
                </Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("Sort", diag.Message);
        Assert.Contains("SpillLevel 2", diag.Message);
        Assert.True(diag.Line > 0);
    }

    [Fact]
    public void Parse_CleanIndexSeekPlan_ReturnsEmpty()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek">
                <IndexScan Ordered="true">
                  <Object Database="[SalesDb]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_CustomerId]" />
                </IndexScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Empty(result);
    }
}
