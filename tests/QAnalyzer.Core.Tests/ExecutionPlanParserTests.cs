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

    [Theory]
    [InlineData("<root><item /></root>")]
    [InlineData("<configuration><appSettings /></configuration>")]
    [InlineData("<html><body>ok</body></html>")]
    public void Parse_NonShowPlanXmlRoot_ThrowsFormatException(string xml)
    {
        var ex = Assert.Throws<FormatException>(() => _parser.Parse(xml));

        Assert.Contains("ShowPlanXML", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("configuration", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("html", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<root>", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_PlanAffectingConvertSeekPlan_ReturnsWarningWithExpression()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple>
                      <QueryPlan>
                        <Warnings>
                          <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(nvarchar(50),[SalesDb].[dbo].[Orders].[OrderCode],0)" />
                        </Warnings>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleImplicitConvert);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("Seek Plan", diag.Message, StringComparison.Ordinal);
        Assert.Contains("CONVERT_IMPLICIT", diag.Message, StringComparison.Ordinal);
        Assert.Contains("Index Seek", diag.Suggestion, StringComparison.Ordinal);
        Assert.True(diag.Line > 0);
    }

    [Fact]
    public void Parse_PlanAffectingConvertCardinalityAndLongExpression_FormatsAndTruncatesCleanly()
    {
        var longExpr = "CONVERT_IMPLICIT(datetime,\r\n\t" + new string('x', 200) + ")";
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <Warnings>
                <PlanAffectingConvert ConvertIssue="Cardinality Estimate" Expression="{longExpr}" />
              </Warnings>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleImplicitConvert);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("Cardinality Estimate", diag.Message, StringComparison.Ordinal);
        Assert.Contains("…", diag.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\r", diag.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", diag.Message, StringComparison.Ordinal);

        const string minimalXml = "<ShowPlanXML><Warnings><PlanAffectingConvert /></Warnings></ShowPlanXML>";
        var minimalDiag = Assert.Single(_parser.Parse(minimalXml), d => d.RuleId == ExecutionPlanParser.RuleImplicitConvert);
        Assert.Equal(RiskLevel.Warning, minimalDiag.Level);
        Assert.False(string.IsNullOrWhiteSpace(minimalDiag.Message));
        Assert.False(string.IsNullOrWhiteSpace(minimalDiag.Suggestion));
    }

    [Fact]
    public void Parse_NoJoinPredicateWarning_ReturnsCriticalWithJoinOperator()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="2" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
                <Warnings NoJoinPredicate="true" />
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleNoJoinPredicate);
        Assert.Equal(RiskLevel.Critical, diag.Level);
        Assert.Contains("Nested Loops", diag.Message, StringComparison.Ordinal);
        Assert.Contains("NoJoinPredicate", diag.Message, StringComparison.Ordinal);
        Assert.Contains("ON", diag.Suggestion, StringComparison.Ordinal);
        Assert.True(diag.Line > 0);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    public void Parse_NoJoinPredicateFalseOrZero_ReturnsEmpty(string flag)
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="2" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
                <Warnings NoJoinPredicate="{flag}" />
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ColumnsWithNoStatistics_ReturnsWarningWithDeduplicatedAndBoundedColumns()
    {
        var xmlWithDupes = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <Warnings>
                <ColumnsWithNoStatistics>
                  <ColumnReference Database="[SalesDb]" Schema="[dbo]" Table="[Orders]" Column="[Status]" />
                  <ColumnReference Database="[SalesDb]" Schema="[DBO]" Table="[orders]" Column="[status]" />
                  <ColumnReference Database="[SalesDb]" Schema="[dbo]" Table="[Orders]" Column="[Region]" />
                </ColumnsWithNoStatistics>
              </Warnings>
            </ShowPlanXML>
            """;

        var diag1 = Assert.Single(_parser.Parse(xmlWithDupes), d => d.RuleId == ExecutionPlanParser.RuleMissingStatistics);
        Assert.Equal(RiskLevel.Warning, diag1.Level);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(diag1.Message, @"dbo\.Orders\.Status", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("dbo.Orders.Region", diag1.Message, StringComparison.Ordinal);
        Assert.Contains("UPDATE STATISTICS", diag1.Suggestion, StringComparison.Ordinal);
        Assert.True(diag1.Line > 0);

        var manyCols = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 7).Select(i => $"<ColumnReference Schema=\"[dbo]\" Table=\"[Orders]\" Column=\"[Col{i}]\" />"));
        var xmlMany = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <Warnings>
                <ColumnsWithNoStatistics>
                  {manyCols}
                </ColumnsWithNoStatistics>
              </Warnings>
            </ShowPlanXML>
            """;

        var diag2 = Assert.Single(_parser.Parse(xmlMany), d => d.RuleId == ExecutionPlanParser.RuleMissingStatistics);
        Assert.Contains("(+2 lainnya)", diag2.Message, StringComparison.Ordinal);
        Assert.Contains("dbo.Orders.Col5", diag2.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.Orders.Col6", diag2.Message, StringComparison.Ordinal);

        const string emptyXml = "<ShowPlanXML><Warnings><ColumnsWithNoStatistics /></Warnings></ShowPlanXML>";
        var diag3 = Assert.Single(_parser.Parse(emptyXml), d => d.RuleId == ExecutionPlanParser.RuleMissingStatistics);
        Assert.Equal(RiskLevel.Warning, diag3.Level);
        Assert.False(string.IsNullOrWhiteSpace(diag3.Message));
    }

    [Fact]
    public void Parse_CombinedWarningsInSinglePlan_DetectsAllWarningTypes()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Hash Match" LogicalOp="Inner Join">
                <Warnings NoJoinPredicate="true">
                  <SpillToTempDb SpillLevel="1" />
                  <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(int,[dbo].[Orders].[Code],0)" />
                  <ColumnsWithNoStatistics>
                    <ColumnReference Schema="[dbo]" Table="[Orders]" Column="[Code]" />
                  </ColumnsWithNoStatistics>
                </Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Equal(4, result.Count);
        Assert.Contains(result, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb);
        Assert.Contains(result, d => d.RuleId == ExecutionPlanParser.RuleNoJoinPredicate);
        Assert.Contains(result, d => d.RuleId == ExecutionPlanParser.RuleImplicitConvert);
        Assert.Contains(result, d => d.RuleId == ExecutionPlanParser.RuleMissingStatistics);
    }

    private static string BuildNestedShowPlan(int totalElementLevels)
    {
        var innerCount = Math.Max(0, totalElementLevels - 1);
        return $"<ShowPlanXML xmlns=\"{ShowPlanNs}\">"
            + string.Concat(Enumerable.Repeat("<Node>", innerCount))
            + string.Concat(Enumerable.Repeat("</Node>", innerCount))
            + "</ShowPlanXML>";
    }

    [Fact]
    public void Parse_XmlExceedingMaxDepth_ThrowsFormatException()
    {
        var atLimitXml = BuildNestedShowPlan(ExecutionPlanParser.MaxXmlDepth);
        var atLimitResult = _parser.Parse(atLimitXml);
        Assert.Empty(atLimitResult);

        var overLimitXml = BuildNestedShowPlan(ExecutionPlanParser.MaxXmlDepth + 1);
        var ex = Assert.Throws<FormatException>(() => _parser.Parse(overLimitXml));
        Assert.Contains(ExecutionPlanParser.MaxXmlDepth.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RidLookupElement_ExtractsTableWithoutDescendingIntoChildRelOp()
    {
        var ridLookupXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="RID Lookup" LogicalOp="RID Lookup">
                <RIDLookup Lookup="true">
                  <Object Database="[SalesDb]" Schema="[dbo]" Table="[HeapOrders]" />
                </RIDLookup>
              </RelOp>
            </ShowPlanXML>
            """;

        var ridResult = _parser.Parse(ridLookupXml);
        var ridDiag = Assert.Single(ridResult, d => d.RuleId == ExecutionPlanParser.RuleKeyLookup);
        Assert.Contains("dbo.HeapOrders", ridDiag.Message, StringComparison.Ordinal);

        var nestedRelOpXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
                <RelOp NodeId="2" PhysicalOp="Index Seek" LogicalOp="Index Seek">
                  <IndexScan Ordered="true">
                    <Object Schema="[dbo]" Table="[ChildOrders]" Index="[IX_Child]" />
                  </IndexScan>
                </RelOp>
              </RelOp>
            </ShowPlanXML>
            """;

        var nestedResult = _parser.Parse(nestedRelOpXml);
        var scanDiag = Assert.Single(nestedResult, d => d.RuleId == ExecutionPlanParser.RuleTableScan);
        Assert.Contains("(tabel tidak diketahui)", scanDiag.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ChildOrders", scanDiag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExcessiveIssues_CapsAtMaxDiagnosticsAndPrioritizesCriticalWarnings()
    {
        var scans = string.Concat(Enumerable.Range(1, ExecutionPlanParser.MaxDiagnostics + 30).Select(i =>
            $"""
            <RelOp NodeId="{i}" PhysicalOp="Table Scan" LogicalOp="Table Scan">
              <TableScan><Object Schema="[dbo]" Table="[T{i}]" /></TableScan>
            </RelOp>
            """));

        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              {scans}
              <RelOp NodeId="9001" PhysicalOp="Sort" LogicalOp="Sort">
                <Warnings><SpillToTempDb SpillLevel="1" /></Warnings>
              </RelOp>
              <RelOp NodeId="9002" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
                <Warnings NoJoinPredicate="true" />
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Equal(ExecutionPlanParser.MaxDiagnostics, result.Count);
        Assert.Contains(result, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb && d.Level == RiskLevel.Critical);
        Assert.Contains(result, d => d.RuleId == ExecutionPlanParser.RuleNoJoinPredicate && d.Level == RiskLevel.Critical);
    }

    [Fact]
    public void Parse_ControlCharsNewlinesAndOversizedAttributes_AreSanitizedAndBounded()
    {
        var oversizedTable = "Orders&#x0A;- fake&#x0D;&#x09;" + new string('X', 250);
        var equalityCols = string.Concat(Enumerable.Range(1, 25).Select(i => $"<Column Name=\"[Col_{i}]\" />"));
        var includeCols = string.Concat(Enumerable.Range(1, 25).Select(i => $"<Column Name=\"[Inc_{i}]\" />"));

        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <MissingIndexes>
                <MissingIndexGroup Impact="95.0">
                  <MissingIndex Schema="[dbo]" Table="[{oversizedTable}]">
                    <ColumnGroup Usage="EQUALITY">{equalityCols}</ColumnGroup>
                    <ColumnGroup Usage="INCLUDE">{includeCols}</ColumnGroup>
                  </MissingIndex>
                </MissingIndexGroup>
              </MissingIndexes>
              <RelOp NodeId="1" PhysicalOp="Sort&#x0A;Injected&#x09;Op" LogicalOp="Sort">
                <Warnings>
                  <SpillToTempDb SpillLevel="2&#x0D;&#x0A;fake" />
                </Warnings>
              </RelOp>
              <RelOp NodeId="2" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek">
                <IndexScan Lookup="true" Ordered="true">
                  <Object Schema="[dbo]" Table="[Orders]" Index="[PK_Orders&#x0A;{new string('I', 200)}]" />
                </IndexScan>
              </RelOp>
              <Warnings>
                <PlanAffectingConvert ConvertIssue="Seek&#x0A;Plan&#x0D;&#x09;Extra" Expression="CONVERT_IMPLICIT(nvarchar(20),&#x0D;&#x0A;&#x09;[dbo].[Orders].[Code],0)" />
              </Warnings>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Equal(4, result.Count);
        foreach (var d in result)
        {
            Assert.DoesNotContain("\r", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\t", d.Message, StringComparison.Ordinal);
            Assert.True(d.Message.Length < 1000, $"Message too long ({d.Message.Length}): {d.Message}");

            if (d.Suggestion is not null)
            {
                Assert.DoesNotContain("\r", d.Suggestion, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", d.Suggestion, StringComparison.Ordinal);
                Assert.DoesNotContain("\t", d.Suggestion, StringComparison.Ordinal);
                Assert.True(d.Suggestion.Length < 1000, $"Suggestion too long ({d.Suggestion.Length}): {d.Suggestion}");
            }
        }

        var missingIndexDiag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleMissingIndex);
        Assert.Contains("…", missingIndexDiag.Message, StringComparison.Ordinal);
        Assert.Contains("Col_16", missingIndexDiag.Suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain("Col_17", missingIndexDiag.Suggestion, StringComparison.Ordinal);
        Assert.Contains("Inc_16", missingIndexDiag.Suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain("Inc_17", missingIndexDiag.Suggestion, StringComparison.Ordinal);

        var lookupDiag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleKeyLookup);
        Assert.Contains("…", lookupDiag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_MemoryGrantWarningExcessiveGrant_ReturnsWarningWithKbMetrics()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple>
                      <QueryPlan>
                        <Warnings>
                          <MemoryGrantWarning GrantWarningKind="Excessive Grant" RequestedMemory="524288" GrantedMemory="524288" MaxUsedMemory="1024" />
                        </Warnings>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleMemoryGrantWarning);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("Excessive Grant", diag.Message, StringComparison.Ordinal);
        Assert.Contains("requested=524288 KB", diag.Message, StringComparison.Ordinal);
        Assert.Contains("granted=524288 KB", diag.Message, StringComparison.Ordinal);
        Assert.Contains("used=1024 KB", diag.Message, StringComparison.Ordinal);
        Assert.Contains("UPDATE STATISTICS", diag.Suggestion, StringComparison.Ordinal);
        Assert.True(diag.Line > 0);
    }

    [Theory]
    [InlineData("Used More Than Granted")]
    [InlineData("Grant Increase")]
    public void Parse_MemoryGrantWarningInsufficientMemory_ReturnsWarningWithSpillGuidance(string kind)
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <Warnings>
                <MemoryGrantWarning GrantWarningKind="{kind}" RequestedMemory="1024" GrantedMemory="1024" MaxUsedMemory="4096" />
              </Warnings>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleMemoryGrantWarning);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains(kind, diag.Message, StringComparison.Ordinal);
        Assert.Contains("spill", diag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(diag.Suggestion));
    }

    [Fact]
    public void Parse_MemoryGrantWarningMinimalOrMalformedMetrics_HandlesGracefully()
    {
        const string xml = """
            <ShowPlanXML>
              <Warnings>
                <MemoryGrantWarning />
                <MemoryGrantWarning GrantWarningKind="Excessive Grant" RequestedMemory="abc" GrantedMemory="-5" MaxUsedMemory="256" />
              </Warnings>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Equal(2, result.Count);
        Assert.All(result, d =>
        {
            Assert.Equal(ExecutionPlanParser.RuleMemoryGrantWarning, d.RuleId);
            Assert.Equal(RiskLevel.Warning, d.Level);
            Assert.False(string.IsNullOrWhiteSpace(d.Message));
            Assert.False(string.IsNullOrWhiteSpace(d.Suggestion));
        });
        Assert.DoesNotContain("abc", result[1].Message, StringComparison.Ordinal);
        Assert.DoesNotContain("-5", result[1].Message, StringComparison.Ordinal);
        Assert.Contains("used=256 KB", result[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnmatchedIndexes_ReturnsWarningWithDeduplicatedAndBoundedObjects()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <Warnings>
                <UnmatchedIndexes>
                  <Parameterization>
                    <Object Database="[SalesDb]" Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Active]" />
                    <Object Database="[SalesDb]" Schema="[DBO]" Table="[orders]" Index="[ix_orders_active]" />
                  </Parameterization>
                </UnmatchedIndexes>
              </Warnings>
            </ShowPlanXML>
            """;

        var diag1 = Assert.Single(_parser.Parse(xml), d => d.RuleId == ExecutionPlanParser.RuleUnmatchedIndex);
        Assert.Equal(RiskLevel.Warning, diag1.Level);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(diag1.Message, @"dbo\.Orders \(IX_Orders_Active\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("OPTION (RECOMPILE)", diag1.Suggestion, StringComparison.Ordinal);
        Assert.True(diag1.Line > 0);

        var manyObjects = string.Join(
            Environment.NewLine,
            Enumerable.Range(1, 7).Select(i => $"<Object Schema=\"[dbo]\" Table=\"[Orders]\" Index=\"[IX_Filter_{i}]\" />"));
        var xmlMany = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <Warnings>
                <UnmatchedIndexes>
                  <Parameterization>
                    {manyObjects}
                  </Parameterization>
                </UnmatchedIndexes>
              </Warnings>
            </ShowPlanXML>
            """;

        var diag2 = Assert.Single(_parser.Parse(xmlMany), d => d.RuleId == ExecutionPlanParser.RuleUnmatchedIndex);
        Assert.Contains("(+2 lainnya)", diag2.Message, StringComparison.Ordinal);
        Assert.Contains("IX_Filter_5", diag2.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("IX_Filter_6", diag2.Message, StringComparison.Ordinal);

        const string emptyXml = "<ShowPlanXML><Warnings><UnmatchedIndexes /></Warnings></ShowPlanXML>";
        var diag3 = Assert.Single(_parser.Parse(emptyXml), d => d.RuleId == ExecutionPlanParser.RuleUnmatchedIndex);
        Assert.Equal(RiskLevel.Warning, diag3.Level);
        Assert.False(string.IsNullOrWhiteSpace(diag3.Message));
        Assert.False(string.IsNullOrWhiteSpace(diag3.Suggestion));
    }

    [Theory]
    [InlineData("TimeOut")]
    [InlineData("MemoryLimitExceeded")]
    public void Parse_OptimizerEarlyAbortTimeoutOrMemoryLimit_ReturnsWarning(string reason)
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="{reason}" />
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleOptimizerEarlyAbort);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains(reason, diag.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(diag.Suggestion));
        Assert.True(diag.Line > 0);
    }

    [Fact]
    public void Parse_OptimizerGoodEnoughPlanFoundOrMissingReason_DoesNotReportAbort()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="GoodEnoughPlanFound" />
                    <StmtSimple StatementOptmLevel="TRIVIAL" />
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Empty(result);
    }

    [Fact]
    public void Parse_EstimateSkewSerialUnderestimateAndOverestimate_ReturnsWarning()
    {
        var underXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="2" PhysicalOp="Clustered Index Scan" LogicalOp="Clustered Index Scan" EstimateRows="10" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        var underDiag = Assert.Single(_parser.Parse(underXml), d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Equal(RiskLevel.Warning, underDiag.Level);
        Assert.Contains("underestimate", underDiag.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5000x", underDiag.Message, StringComparison.Ordinal);
        Assert.Contains("10", underDiag.Message, StringComparison.Ordinal);
        Assert.Contains("50000", underDiag.Message, StringComparison.Ordinal);
        Assert.Contains("Clustered Index Scan", underDiag.Message, StringComparison.Ordinal);
        Assert.Contains("NodeId 2", underDiag.Message, StringComparison.Ordinal);
        Assert.True(underDiag.Line > 0);

        var overXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="3" PhysicalOp="Hash Match" LogicalOp="Inner Join" EstimateRows="50000" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="0" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        var overDiag = Assert.Single(_parser.Parse(overXml), d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Equal(RiskLevel.Warning, overDiag.Level);
        Assert.Contains("overestimate", overDiag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EstimateSkewScientificNotationAndZeroEstimate_HandledSafely()
    {
        var sciXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1.5E+06" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="10" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        var sciDiag = Assert.Single(_parser.Parse(sciXml), d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Contains("overestimate", sciDiag.Message, StringComparison.OrdinalIgnoreCase);

        var zeroEstXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="2" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="0" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="2500" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        var zeroDiag = Assert.Single(_parser.Parse(zeroEstXml), d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Contains("underestimate", zeroDiag.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EstimateSkewParallelSingleExecution_FlagsSkewOnlyWhenTotalActualSkews()
    {
        var skewedParallelXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="4" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="10" Parallel="true">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="0" ActualExecutions="0" />
                  <RunTimeCountersPerThread Thread="1" ActualRows="6250" ActualExecutions="1" />
                  <RunTimeCountersPerThread Thread="2" ActualRows="6250" ActualExecutions="1" />
                  <RunTimeCountersPerThread Thread="3" ActualRows="6250" ActualExecutions="1" />
                  <RunTimeCountersPerThread Thread="4" ActualRows="6250" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        var diag = Assert.Single(_parser.Parse(skewedParallelXml), d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("25000", diag.Message, StringComparison.Ordinal);
        Assert.Contains("10", diag.Message, StringComparison.Ordinal);

        var accurateParallelXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="5" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="10000" Parallel="true">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="1" ActualRows="2500" ActualExecutions="1" />
                  <RunTimeCountersPerThread Thread="2" ActualRows="2500" ActualExecutions="1" />
                  <RunTimeCountersPerThread Thread="3" ActualRows="2500" ActualExecutions="1" />
                  <RunTimeCountersPerThread Thread="4" ActualRows="2500" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        Assert.Empty(_parser.Parse(accurateParallelXml));
    }

    [Fact]
    public void Parse_EstimateSkewIgnoresAccurateMultiExecutionUnexecutedAmbiguousParallelAndBelowThreshold()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="10" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="1000" ActualExecutions="100" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="2" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="50000" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="0" ActualExecutions="0" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="3" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="10" Parallel="true">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="1" ActualRows="5000" ActualExecutions="5" />
                  <RunTimeCountersPerThread Thread="2" ActualRows="5000" ActualExecutions="5" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="4" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="1" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="500" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="5" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="10000" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        Assert.Empty(_parser.Parse(xml));
    }

    [Fact]
    public void Parse_EstimateSkewNestedRelOp_AttributesRuntimeInfoOnlyToDirectChildRelOp()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="10" Parallel="false">
                <NestedLoops>
                  <RelOp NodeId="2" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="5" Parallel="false">
                    <RunTimeInformation>
                      <RunTimeCountersPerThread Thread="0" ActualRows="25000" ActualExecutions="1" />
                    </RunTimeInformation>
                  </RelOp>
                </NestedLoops>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Contains("NodeId 2", diag.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("NodeId 1", diag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExchangeSpillDetails_DetectsStandaloneCriticalSpillWithoutDoubleReportingAndRespectsCap()
    {
        var standaloneXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="7" PhysicalOp="Parallelism" LogicalOp="Repartition Streams">
                <Warnings>
                  <ExchangeSpillDetails WritesToTempDb="42" />
                </Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var standaloneDiag = Assert.Single(_parser.Parse(standaloneXml), d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb);
        Assert.Equal(RiskLevel.Critical, standaloneDiag.Level);
        Assert.Contains("Parallelism", standaloneDiag.Message, StringComparison.Ordinal);
        Assert.Contains("42", standaloneDiag.Message, StringComparison.Ordinal);

        var combinedSameWarningsXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="8" PhysicalOp="Parallelism" LogicalOp="Repartition Streams">
                <Warnings>
                  <SpillToTempDb SpillLevel="1" />
                  <ExchangeSpillDetails WritesToTempDb="42" />
                </Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        Assert.Single(_parser.Parse(combinedSameWarningsXml), d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb);

        var scans = string.Concat(Enumerable.Range(1, ExecutionPlanParser.MaxDiagnostics + 20).Select(i =>
            $"""
            <RelOp NodeId="{i}" PhysicalOp="Table Scan" LogicalOp="Table Scan">
              <TableScan><Object Schema="[dbo]" Table="[T{i}]" /></TableScan>
            </RelOp>
            """));

        var floodedXml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              {scans}
              <RelOp NodeId="9001" PhysicalOp="Parallelism" LogicalOp="Repartition Streams">
                <Warnings><ExchangeSpillDetails WritesToTempDb="19" /></Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var floodedResult = _parser.Parse(floodedXml);
        Assert.Equal(ExecutionPlanParser.MaxDiagnostics, floodedResult.Count);
        Assert.Contains(floodedResult, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb && d.Level == RiskLevel.Critical);
    }

    [Fact]
    public void Parse_AdvancedDiagnosticsControlCharsAndOversizedValues_AreSanitizedAndBounded()
    {
        var longTable = "Orders&#x0A;&#x09;" + new string('T', 200);
        var longIndex = "IX_Orders&#x0D;&#x0A;" + new string('I', 200);
        var longOp = "Hash Match&#x0A;&#x09;" + new string('O', 120);
        var longNode = "99&#x0A;&#x09;" + new string('N', 120);
        var longKind = "Excessive Grant&#x0A;&#x09;" + new string('K', 120);

        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple StatementOptmEarlyAbortReason="TimeOut">
                      <QueryPlan>
                        <Warnings>
                          <MemoryGrantWarning GrantWarningKind="{longKind}" RequestedMemory="1024" GrantedMemory="1024" MaxUsedMemory="64" />
                          <UnmatchedIndexes>
                            <Parameterization>
                              <Object Schema="[dbo]" Table="[{longTable}]" Index="[{longIndex}]" />
                            </Parameterization>
                          </UnmatchedIndexes>
                        </Warnings>
                        <RelOp NodeId="{longNode}" PhysicalOp="{longOp}" LogicalOp="Inner Join" EstimateRows="5" Parallel="false">
                          <RunTimeInformation>
                            <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualExecutions="1" />
                          </RunTimeInformation>
                        </RelOp>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Equal(4, result.Count);
        foreach (var d in result)
        {
            Assert.DoesNotContain("\r", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", d.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("\t", d.Message, StringComparison.Ordinal);
            Assert.True(d.Message.Length < 1000, $"Message too long ({d.Message.Length}): {d.Message}");

            if (d.Suggestion is not null)
            {
                Assert.DoesNotContain("\r", d.Suggestion, StringComparison.Ordinal);
                Assert.DoesNotContain("\n", d.Suggestion, StringComparison.Ordinal);
                Assert.DoesNotContain("\t", d.Suggestion, StringComparison.Ordinal);
                Assert.True(d.Suggestion.Length < 1000, $"Suggestion too long ({d.Suggestion.Length}): {d.Suggestion}");
            }
        }

        Assert.Contains("…", Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleMemoryGrantWarning).Message, StringComparison.Ordinal);
        Assert.Contains("…", Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleUnmatchedIndex).Message, StringComparison.Ordinal);
        Assert.Contains("…", Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_OptimizerEarlyAbortOnStmtCond_ReturnsWarning()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtCond StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="TimeOut" />
                    <StmtCond StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="GoodEnoughPlanFound" />
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleOptimizerEarlyAbort);
        Assert.Single(result);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("TimeOut", diag.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(diag.Suggestion));
        Assert.True(diag.Line > 0);
    }

    [Fact]
    public void Parse_EstimateSkewSerialMultiExecutionAndMalformedRuntimeNumbers_HandlesAccurately()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="10" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualExecutions="100" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="11" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="NaN" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="12" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="-5" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="13" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="Infinity" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="50000" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="14" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="1" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="abc" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
              <RelOp NodeId="15" PhysicalOp="Filter" LogicalOp="Filter" EstimateRows="1" Parallel="false">
                <RunTimeInformation>
                  <RunTimeCountersPerThread Thread="0" ActualRows="-10000" ActualExecutions="1" />
                </RunTimeInformation>
              </RelOp>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        var diag = Assert.Single(result, d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Single(result);
        Assert.Equal(RiskLevel.Warning, diag.Level);
        Assert.Contains("500x", diag.Message, StringComparison.Ordinal);
        Assert.Contains("100", diag.Message, StringComparison.Ordinal);
        Assert.Contains("50000", diag.Message, StringComparison.Ordinal);
        Assert.Contains("NodeId 10", diag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_FloodedPlanWithAdvancedDiagnostics_CapsAtMaxDiagnosticsAndPreservesCriticals()
    {
        var scans = string.Concat(Enumerable.Range(1, ExecutionPlanParser.MaxDiagnostics + 15).Select(i =>
            $"""
            <RelOp NodeId="{i}" PhysicalOp="Table Scan" LogicalOp="Table Scan">
              <TableScan><Object Schema="[dbo]" Table="[T{i}]" /></TableScan>
            </RelOp>
            """));

        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple StatementOptmEarlyAbortReason="TimeOut">
                      <QueryPlan>
                        <Warnings>
                          <MemoryGrantWarning GrantWarningKind="Excessive Grant" RequestedMemory="65536" GrantedMemory="65536" MaxUsedMemory="512" />
                          <UnmatchedIndexes>
                            <Parameterization>
                              <Object Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Open]" />
                            </Parameterization>
                          </UnmatchedIndexes>
                        </Warnings>
                        {scans}
                        <RelOp NodeId="9001" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="5" Parallel="false">
                          <RunTimeInformation>
                            <RunTimeCountersPerThread Thread="0" ActualRows="25000" ActualExecutions="1" />
                          </RunTimeInformation>
                        </RelOp>
                        <RelOp NodeId="9002" PhysicalOp="Parallelism" LogicalOp="Repartition Streams">
                          <Warnings>
                            <ExchangeSpillDetails WritesToTempDb="7" />
                          </Warnings>
                        </RelOp>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var result = _parser.Parse(xml);

        Assert.Equal(ExecutionPlanParser.MaxDiagnostics, result.Count);
        Assert.Contains(result, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb && d.Level == RiskLevel.Critical);
    }
}
