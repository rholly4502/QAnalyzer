using QAnalyzer.Core;

public sealed class SqlDmvReaderTests
{
    private sealed class FakeReader : ISqlMetadataReader
    {
        public bool IsConnected => true;
        public IReadOnlyList<QueryExecutionStat> GetTopQueryStats(int top) =>
            [ new("SELECT * FROM t", 10, 1200, 3000, 50000) ];
        public IReadOnlyList<IndexUsage> GetIndexUsage(string? schema = null, string? table = null) =>
            [ new("dbo", "Orders", "IX_Order_Date", 100, 5, 0, 10) ];
        public CachedQueryStat? GetStatsForSql(string sqlText) =>
            new(sqlText, 12, 4500, 2000, 80000, 33);
        public IReadOnlyList<MissingIndex> GetMissingIndexes(string? dbName = null) =>
            [ new("AdventureWorks", "dbo", "Orders", "CustomerId", null, "Name,Total", 500, 20, 85) ];
        public ExecutionPlanResult? ExecutionPlanToReturn { get; set; } =
            new("<ShowPlanXML />", ExecutionPlanSource.PlanCache);
        public CancellationToken LastPlanCancellationToken { get; private set; }
        public Task<ExecutionPlanResult?> GetExecutionPlanXmlAsync(string sqlText, CancellationToken ct = default)
        {
            LastPlanCancellationToken = ct;
            return Task.FromResult(ExecutionPlanToReturn);
        }
        public void Dispose() { }
    }

    [Fact]
    public void FakeReader_ReturnsStatsAndIndexUsage()
    {
        using var reader = new FakeReader();
        Assert.True(reader.IsConnected);
        var q = reader.GetTopQueryStats(5);
        Assert.Single(q);
        Assert.Equal(50000L, q[0].TotalLogicalReads);
        var idx = reader.GetIndexUsage();
        Assert.Single(idx);
        Assert.Equal("IX_Order_Date", idx[0].Index);
    }

    [Fact]
    public void FakeReader_GetStatsForSql_ReturnsCachedStat()
    {
        using var reader = new FakeReader();
        var stat = reader.GetStatsForSql("SELECT * FROM dbo.Orders t id=@id");
        Assert.NotNull(stat);
        Assert.Equal(12L, stat!.ExecutionCount);
        Assert.Equal(4500L, stat.TotalElapsedMs);
        Assert.Equal(33, stat.PlanAgeMinutes);
    }

    [Fact]
    public void FakeReader_GetMissingIndexes_ReturnsSuggestedIndex()
    {
        using var reader = new FakeReader();
        var idx = reader.GetMissingIndexes("AdventureWorks");
        Assert.Single(idx);
        Assert.Equal("dbo", idx[0].Schema);
        Assert.Equal("Orders", idx[0].Table);
        Assert.Equal(85L, idx[0].AvgUserImpact);
    }

    [Fact]
    public async Task FakeReader_GetExecutionPlanXmlAsync_ReturnsPlanAndForwardsCancellationToken()
    {
        using var reader = new FakeReader
        {
            ExecutionPlanToReturn = new ExecutionPlanResult("<ShowPlanXML><RelOp /></ShowPlanXML>", ExecutionPlanSource.EstimatedShowPlan),
        };
        using var cts = new CancellationTokenSource();

        var result = await reader.GetExecutionPlanXmlAsync("SELECT 1;", cts.Token);

        Assert.NotNull(result);
        Assert.Equal(ExecutionPlanSource.EstimatedShowPlan, result!.Source);
        Assert.Contains("<ShowPlanXML>", result.PlanXml);
        Assert.Equal(cts.Token, reader.LastPlanCancellationToken);
    }

    [Fact]
    public async Task SqlDmvReader_GetExecutionPlanXmlAsync_NullSql_ThrowsArgumentNullException()
    {
        using var reader = new SqlDmvReader("Server=localhost;Database=master;Integrated Security=true;");
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.GetExecutionPlanXmlAsync(null!));
    }

    [Fact]
    public async Task SqlDmvReader_GetExecutionPlanXmlAsync_WhitespaceSql_ReturnsNullWithoutOpeningConnection()
    {
        using var reader = new SqlDmvReader("Server=127.0.0.1,1;Database=master;Connect Timeout=1;");
        var result = await reader.GetExecutionPlanXmlAsync("   \r\n\t ");

        Assert.Null(result);
        Assert.False(reader.IsConnected);
    }

    [Fact]
    public async Task SqlDmvReader_GetExecutionPlanXmlAsync_CanceledToken_ThrowsOperationCanceledException()
    {
        using var reader = new SqlDmvReader("Server=127.0.0.1,1;Database=master;Connect Timeout=1;");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.GetExecutionPlanXmlAsync("SELECT 1;", cts.Token));
        Assert.False(reader.IsConnected);
    }
}
