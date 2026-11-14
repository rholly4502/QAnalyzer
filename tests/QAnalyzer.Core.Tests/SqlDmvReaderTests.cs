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
}
