using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

public sealed class LocalExecutionPlanTests
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private sealed class StubMetadataReader(ExecutionPlanResult? planResult) : ISqlMetadataReader
    {
        public bool IsConnected => true;
        public string? LastSql { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public IReadOnlyList<QueryExecutionStat> GetTopQueryStats(int top) => [];
        public IReadOnlyList<IndexUsage> GetIndexUsage(string? schema = null, string? table = null) => [];
        public CachedQueryStat? GetStatsForSql(string sqlText) => null;
        public IReadOnlyList<MissingIndex> GetMissingIndexes(string? dbName = null) => [];

        public Task<ExecutionPlanResult?> GetExecutionPlanXmlAsync(string sqlText, CancellationToken ct = default)
        {
            LastSql = sqlText;
            LastToken = ct;
            return Task.FromResult(planResult);
        }

        public void Dispose() { }
    }

    [Theory]
    [InlineData("SELECT Id, Total FROM dbo.Orders WHERE CustomerId = 42;")]
    [InlineData("WITH Recent AS (SELECT TOP 10 Id FROM dbo.Orders) SELECT Id FROM Recent UNION ALL SELECT 0;")]
    [InlineData("DECLARE @id INT = 1; SET @id = 2; SELECT Id FROM dbo.Orders WHERE CustomerId = @id;")]
    [InlineData("-- DELETE FROM dbo.Orders\nSELECT 'UPDATE dbo.Orders SET X=1; DROP TABLE dbo.Orders;' AS Msg;")]
    public void CanSafelyEstimatePlan_AllowsReadOnlySelectScripts(string sql)
    {
        var allowed = ExecutionPlanQueryGuard.CanSafelyEstimatePlan(sql, out var reason);

        Assert.True(allowed, $"Seharusnya diizinkan, namun ditolak: {reason}");
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    [InlineData("SELECT FROM WHERE")]
    [InlineData("DECLARE @x INT = 1;")]
    [InlineData("INSERT INTO dbo.Orders (Id) VALUES (1);")]
    [InlineData("UPDATE dbo.Orders SET Total = 0 WHERE Id = 1;")]
    [InlineData("DELETE FROM dbo.Orders WHERE Id = 1;")]
    [InlineData("MERGE dbo.Orders AS t USING dbo.Stage AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;")]
    [InlineData("TRUNCATE TABLE dbo.Orders;")]
    [InlineData("DROP TABLE dbo.Orders;")]
    [InlineData("CREATE TABLE dbo.TempDemo (Id INT);")]
    [InlineData("ALTER TABLE dbo.Orders ADD Flag BIT NULL;")]
    [InlineData("EXEC dbo.sp_DoSomething @id = 1;")]
    [InlineData("SELECT * INTO dbo.OrdersCopy FROM dbo.Orders;")]
    [InlineData("SELECT * FROM (DELETE FROM dbo.Orders OUTPUT DELETED.Id WHERE Id = 1) AS d;")]
    [InlineData("SET SHOWPLAN_XML OFF; SELECT 1;")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1') AS r;")]
    [InlineData("SELECT * FROM OPENQUERY(LinkedSrv, 'SELECT 1');")]
    [InlineData("SELECT 1;\nGO\nSELECT 2;")]
    public void CanSafelyEstimatePlan_RejectsMutationsDdlMultiBatchAndInvalidSql(string sql)
    {
        var allowed = ExecutionPlanQueryGuard.CanSafelyEstimatePlan(sql, out var reason);

        Assert.False(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void BuildPlanCacheLikePattern_NormalizesLiteralsAndEscapesLikeMetacharacters()
    {
        var sql = "  SELECT [Col_1] FROM dbo.Orders_2025 WHERE Name = N'O''Reilly' AND Note LIKE '%disc%' AND Amount > 125.50;  ";

        var pattern = ExecutionPlanQueryGuard.BuildPlanCacheLikePattern(sql);

        Assert.StartsWith("%SELECT \\[Col\\_1] FROM dbo.Orders\\_2025", pattern);
        Assert.Contains("Name = % AND Note LIKE % AND Amount > %", pattern);
        Assert.DoesNotContain("O''Reilly", pattern);
        Assert.DoesNotContain("125.50", pattern);
        Assert.EndsWith("%", pattern);
        Assert.DoesNotContain("%%", pattern);
    }

    [Fact]
    public void BuildPlanCacheLikePattern_TruncatesLongSqlWithoutBreakingEscapeSequence()
    {
        var longCols = string.Join(", ", Enumerable.Range(1, 50).Select(i => $"Col_{i}"));
        var sql = $"SELECT {longCols} FROM dbo.Large_Table WHERE Id = 99;";

        var pattern = ExecutionPlanQueryGuard.BuildPlanCacheLikePattern(sql);

        Assert.False(string.IsNullOrEmpty(pattern));
        Assert.StartsWith("%SELECT Col\\_1", pattern);
        Assert.EndsWith("%", pattern);
        Assert.False(pattern.EndsWith(@"\"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("'only literal'")]
    public void BuildPlanCacheLikePattern_ReturnsEmptyForWhitespaceOrLiteralOnly(string sql)
    {
        var pattern = ExecutionPlanQueryGuard.BuildPlanCacheLikePattern(sql);

        Assert.Equal(string.Empty, pattern);
    }

    [Fact]
    public async Task LocalExecutionPlanAnalyzer_CombinesStaticAndAllPlanDiagnostics()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <BatchSequence>
                <Batch>
                  <Statements>
                    <StmtSimple>
                      <QueryPlan>
                        <MissingIndexes>
                          <MissingIndexGroup Impact="91.2">
                            <MissingIndex Database="[Sales]" Schema="[dbo]" Table="[Orders]">
                              <ColumnGroup Usage="EQUALITY">
                                <Column Name="[CustomerId]" ColumnId="2" />
                              </ColumnGroup>
                            </MissingIndex>
                          </MissingIndexGroup>
                        </MissingIndexes>
                        <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
                          <TableScan>
                            <Object Database="[Sales]" Schema="[dbo]" Table="[AuditLog]" />
                          </TableScan>
                        </RelOp>
                        <RelOp NodeId="2" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek">
                          <IndexScan Lookup="true" Ordered="true">
                            <Object Database="[Sales]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]" />
                          </IndexScan>
                        </RelOp>
                        <RelOp NodeId="3" PhysicalOp="Sort" LogicalOp="Sort">
                          <Warnings>
                            <SpillToTempDb SpillLevel="1" />
                          </Warnings>
                        </RelOp>
                      </QueryPlan>
                    </StmtSimple>
                  </Statements>
                </Batch>
              </BatchSequence>
            </ShowPlanXML>
            """;

        var reader = new StubMetadataReader(new ExecutionPlanResult(xml, ExecutionPlanSource.PlanCache));
        var analyzer = new LocalExecutionPlanAnalyzer([new SelectStarRule()]);

        var result = await analyzer.AnalyzeAsync("SELECT * FROM dbo.Orders;", reader);

        Assert.Equal(ExecutionPlanSource.PlanCache, result.PlanSource);
        Assert.Equal(4, result.PlanDiagnosticCount);
        Assert.Null(result.PlanNote);
        Assert.Contains(result.Diagnostics, d => d.RuleId == "SELECT_STAR");
        Assert.Contains(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleMissingIndex);
        Assert.Contains(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleKeyLookup);
        Assert.Contains(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleTableScan);
        Assert.Contains(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb);
    }

    [Fact]
    public async Task LocalExecutionPlanAnalyzer_RespectsSeverityOverridesAndForwardsCancellationToken()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
                <TableScan>
                  <Object Schema="[dbo]" Table="[HeapTable]" />
                </TableScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var settings = new AnalyzerSettings
        {
            RuleSeverityOverrides = new Dictionary<string, RiskLevel>
            {
                [ExecutionPlanParser.RuleTableScan] = RiskLevel.Critical,
            },
        };
        var reader = new StubMetadataReader(new ExecutionPlanResult(xml, ExecutionPlanSource.EstimatedShowPlan));
        var analyzer = new LocalExecutionPlanAnalyzer([new SelectStarRule()], settings);
        using var cts = new CancellationTokenSource();

        var result = await analyzer.AnalyzeAsync("SELECT Id FROM dbo.HeapTable;", reader, cts.Token);

        Assert.Equal(ExecutionPlanSource.EstimatedShowPlan, result.PlanSource);
        Assert.Equal(cts.Token, reader.LastToken);
        var scanDiag = Assert.Single(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleTableScan);
        Assert.Equal(RiskLevel.Critical, scanDiag.Level);
    }

    [Fact]
    public async Task LocalExecutionPlanAnalyzer_WhenNoPlanForMutation_ReturnsSecurityNote()
    {
        var reader = new StubMetadataReader(null);
        var analyzer = new LocalExecutionPlanAnalyzer([new UpdateWithoutWhereRule()]);

        var result = await analyzer.AnalyzeAsync("UPDATE dbo.Orders SET Total = 0;", reader);

        Assert.Null(result.PlanSource);
        Assert.Equal(0, result.PlanDiagnosticCount);
        Assert.Contains("keamanan", result.PlanNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Diagnostics, d => d.RuleId == "UPDATE_NO_WHERE");
    }

    [Fact]
    public async Task LocalExecutionPlanAnalyzer_WhenNoPlanForReadOnlySelect_ReturnsNotFoundNote()
    {
        var reader = new StubMetadataReader(null);
        var analyzer = new LocalExecutionPlanAnalyzer([new SelectStarRule()]);

        var result = await analyzer.AnalyzeAsync("SELECT * FROM dbo.Orders;", reader);

        Assert.Null(result.PlanSource);
        Assert.Equal(0, result.PlanDiagnosticCount);
        Assert.Contains("belum tersedia", result.PlanNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Diagnostics, d => d.RuleId == "SELECT_STAR");
    }

    [Fact]
    public async Task LocalExecutionPlanAnalyzer_MalformedPlanXml_ThrowsFormatException()
    {
        var reader = new StubMetadataReader(new ExecutionPlanResult("<ShowPlanXML><broken>", ExecutionPlanSource.PlanCache));
        var analyzer = new LocalExecutionPlanAnalyzer([new SelectStarRule()]);

        await Assert.ThrowsAsync<FormatException>(() => analyzer.AnalyzeAsync("SELECT * FROM dbo.Orders;", reader));
    }

    [Fact]
    public async Task LocalExecutionPlanAnalyzer_DetectsNewPlanWarningsAndAppliesOverrides()
    {
        var xml = $"""
            <ShowPlanXML xmlns="{ShowPlanNs}">
              <RelOp NodeId="1" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
                <Warnings NoJoinPredicate="true">
                  <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(nvarchar(50),[dbo].[Orders].[Code],0)" />
                  <ColumnsWithNoStatistics>
                    <ColumnReference Schema="[dbo]" Table="[Orders]" Column="[Status]" />
                  </ColumnsWithNoStatistics>
                </Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var settings = new AnalyzerSettings
        {
            RuleSeverityOverrides = new Dictionary<string, RiskLevel>
            {
                [ExecutionPlanParser.RuleImplicitConvert] = RiskLevel.Critical,
            },
        };
        var reader = new StubMetadataReader(new ExecutionPlanResult(xml, ExecutionPlanSource.PlanCache));
        var analyzer = new LocalExecutionPlanAnalyzer([new SelectStarRule()], settings);

        var result = await analyzer.AnalyzeAsync("SELECT Id FROM dbo.Orders;", reader);

        Assert.Equal(ExecutionPlanSource.PlanCache, result.PlanSource);
        Assert.Equal(3, result.PlanDiagnosticCount);
        var convertDiag = Assert.Single(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleImplicitConvert);
        Assert.Equal(RiskLevel.Critical, convertDiag.Level);
        Assert.Contains(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleNoJoinPredicate && d.Level == RiskLevel.Critical);
        Assert.Contains(result.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleMissingStatistics && d.Level == RiskLevel.Warning);
    }
}
