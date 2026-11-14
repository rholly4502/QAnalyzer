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
}
