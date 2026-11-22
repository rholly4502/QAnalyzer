using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.TransactSql.ScriptDom;
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

    private sealed class ReadOnlyAstGuardVisitor : TSqlFragmentVisitor
    {
        public bool HasUnsafeNode { get; private set; }

        public override void Visit(DataModificationStatement node) => HasUnsafeNode = true;
        public override void Visit(DataModificationTableReference node) => HasUnsafeNode = true;
        public override void Visit(ExecuteSpecification node) => HasUnsafeNode = true;
        public override void Visit(PredicateSetStatement node) => HasUnsafeNode = true;
        public override void Visit(SelectStatement node)
        {
            if (node.Into is not null)
            {
                HasUnsafeNode = true;
            }
        }
    }

    private sealed class FakeDataRecord(params object?[] values) : IDataRecord
    {
        public int FieldCount => values.Length;
        public object this[int i] => values[i] ?? DBNull.Value;
        public object this[string name] => throw new NotSupportedException();
        public bool GetBoolean(int i) => Convert.ToBoolean(values[i]);
        public byte GetByte(int i) => Convert.ToByte(values[i]);
        public long GetBytes(int i, long fieldOffset, byte[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public char GetChar(int i) => Convert.ToChar(values[i]);
        public long GetChars(int i, long fieldoffset, char[]? buffer, int bufferoffset, int length) => throw new NotSupportedException();
        public IDataReader GetData(int i) => throw new NotSupportedException();
        public string GetDataTypeName(int i) => values[i]?.GetType().Name ?? nameof(DBNull);
        public DateTime GetDateTime(int i) => Convert.ToDateTime(values[i]);
        public decimal GetDecimal(int i) => Convert.ToDecimal(values[i]);
        public double GetDouble(int i) => Convert.ToDouble(values[i]);
        public Type GetFieldType(int i) => values[i]?.GetType() ?? typeof(object);
        public float GetFloat(int i) => Convert.ToSingle(values[i]);
        public Guid GetGuid(int i) => (Guid)values[i]!;
        public short GetInt16(int i) => Convert.ToInt16(values[i]);
        public int GetInt32(int i) => Convert.ToInt32(values[i]);
        public long GetInt64(int i) => Convert.ToInt64(values[i]);
        public string GetName(int i) => $"col{i}";
        public int GetOrdinal(string name) => throw new NotSupportedException();
        public string GetString(int i) => (string)values[i]!;
        public object GetValue(int i) => values[i] ?? DBNull.Value;
        public int GetValues(object[] target)
        {
            var count = Math.Min(target.Length, values.Length);
            for (var i = 0; i < count; i++)
            {
                target[i] = values[i] ?? DBNull.Value;
            }
            return count;
        }
        public bool IsDBNull(int i) => values[i] is null || values[i] is DBNull;
    }

    private static void AssertValidReadOnlySelectSql(string sql)
    {
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        Assert.Empty(errors);
        var script = Assert.IsType<TSqlScript>(fragment);
        Assert.NotEmpty(script.Batches);

        var visitor = new ReadOnlyAstGuardVisitor();
        foreach (var batch in script.Batches)
        {
            var stmt = Assert.Single(batch.Statements);
            var select = Assert.IsType<SelectStatement>(stmt);
            Assert.Null(select.Into);
            stmt.Accept(visitor);
        }

        Assert.False(visitor.HasUnsafeNode);
    }

    [Fact]
    public void Constructor_NullOrWhitespaceConnectionString_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentNullException>(() => new SqlDmvReader(null!));
        Assert.Throws<ArgumentException>(() => new SqlDmvReader(string.Empty));
        Assert.Throws<ArgumentException>(() => new SqlDmvReader("   \t\r\n  "));
    }

    [Fact]
    public void DmvSqlCommands_ParseWithoutSyntaxErrors_AndAreStrictlyReadOnlySelects()
    {
        AssertValidReadOnlySelectSql(SqlDmvReader.TopQueryStatsSql);
        AssertValidReadOnlySelectSql(SqlDmvReader.StatsForSqlQuerySql);
        AssertValidReadOnlySelectSql(SqlDmvReader.MissingIndexesQuerySql);
        AssertValidReadOnlySelectSql(SqlDmvReader.CachedPlanQuerySql);

        using var noFilterCmd = SqlDmvReader.CreateIndexUsageCommand(null);
        using var schemaOnlyCmd = SqlDmvReader.CreateIndexUsageCommand(null, schema: "dbo");
        using var tableOnlyCmd = SqlDmvReader.CreateIndexUsageCommand(null, table: "Orders");
        using var bothCmd = SqlDmvReader.CreateIndexUsageCommand(null, schema: "dbo", table: "Orders");

        Assert.NotNull(noFilterCmd);
        Assert.NotNull(schemaOnlyCmd);
        Assert.NotNull(tableOnlyCmd);
        Assert.NotNull(bothCmd);

        AssertValidReadOnlySelectSql(noFilterCmd!.CommandText);
        AssertValidReadOnlySelectSql(schemaOnlyCmd!.CommandText);
        AssertValidReadOnlySelectSql(tableOnlyCmd!.CommandText);
        AssertValidReadOnlySelectSql(bothCmd!.CommandText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(SqlDmvReader.MaxTopQueryStats + 1)]
    public void TopQueryStats_InvalidTop_ThrowsArgumentOutOfRangeWithoutOpeningConnection(int invalidTop)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlDmvReader.CreateTopQueryStatsCommand(null, invalidTop));

        using var reader = new SqlDmvReader("Server=127.0.0.1,1;Database=master;Connect Timeout=1;");
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetTopQueryStats(invalidTop));
        Assert.False(reader.IsConnected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    [InlineData(SqlDmvReader.MaxTopQueryStats)]
    public void CreateTopQueryStatsCommand_ValidTop_SetsTypedParamTimeoutAndSafeGuards(int top)
    {
        using var cmd = SqlDmvReader.CreateTopQueryStatsCommand(null, top);

        Assert.Equal(SqlDmvReader.DefaultCommandTimeoutSeconds, cmd.CommandTimeout);
        var topParam = Assert.Single(cmd.Parameters.Cast<SqlParameter>());
        Assert.Equal("@top", topParam.ParameterName);
        Assert.Equal(SqlDbType.Int, topParam.SqlDbType);
        Assert.Equal(top, topParam.Value);

        Assert.Contains("st.text IS NOT NULL", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("sys.dm_exec_query_stats", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("statement_end_offset < qs.statement_start_offset", cmd.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("GREATEST", cmd.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StatsForSql_NullWhitespaceAndLiteralOnly_RejectsOrReturnsNullWithoutConnection()
    {
        Assert.Throws<ArgumentNullException>(() => SqlDmvReader.CreateStatsForSqlCommand(null, null!));

        using var reader = new SqlDmvReader("Server=127.0.0.1,1;Database=master;Connect Timeout=1;");
        Assert.Throws<ArgumentNullException>(() => reader.GetStatsForSql(null!));

        foreach (var input in new[] { "   ", "12345", "'abc'" })
        {
            Assert.Null(SqlDmvReader.CreateStatsForSqlCommand(null, input));
            Assert.Null(reader.GetStatsForSql(input));
        }

        Assert.False(reader.IsConnected);
    }

    [Fact]
    public void CreateStatsForSqlCommand_RealSql_UsesQsCreationTimeAndEscapedLikePattern()
    {
        const string sql = "SELECT [Col_1] FROM dbo.Orders_2025 WHERE Id % 2 = 0 AND Name = N'O''Reilly';";
        var expectedPattern = ExecutionPlanQueryGuard.BuildPlanCacheLikePattern(sql);

        using var cmd = SqlDmvReader.CreateStatsForSqlCommand(null, sql);

        Assert.NotNull(cmd);
        Assert.Equal(SqlDmvReader.DefaultCommandTimeoutSeconds, cmd!.CommandTimeout);

        var patternParam = Assert.Single(cmd.Parameters.Cast<SqlParameter>());
        Assert.Equal("@pattern", patternParam.ParameterName);
        Assert.Equal(SqlDbType.NVarChar, patternParam.SqlDbType);
        Assert.Equal(4000, patternParam.Size);
        Assert.Equal(expectedPattern, patternParam.Value);
        Assert.Contains(@"\_", expectedPattern, StringComparison.Ordinal);
        Assert.Contains(@"\[", expectedPattern, StringComparison.Ordinal);
        Assert.Contains(@"\%", expectedPattern, StringComparison.Ordinal);
        Assert.DoesNotContain("?", expectedPattern, StringComparison.Ordinal);

        Assert.Contains("qs.creation_time", cmd.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("cp.creation_time", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains(@"LIKE @pattern ESCAPE '\'", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("st.text NOT LIKE '%sys.dm_exec_query_stats%'", cmd.CommandText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateMissingIndexesCommand_NullOrWhitespaceDbName_UsesCurrentDatabaseFallback(string? dbName)
    {
        using var cmd = SqlDmvReader.CreateMissingIndexesCommand(null, dbName);

        Assert.NotNull(cmd);
        Assert.Equal(SqlDmvReader.DefaultCommandTimeoutSeconds, cmd!.CommandTimeout);

        var dbParam = cmd.Parameters["@db"];
        Assert.Equal(SqlDbType.NVarChar, dbParam.SqlDbType);
        Assert.Equal(SqlDmvReader.MaxSqlIdentifierLength, dbParam.Size);
        Assert.Equal(DBNull.Value, dbParam.Value);

        var topParam = cmd.Parameters["@top"];
        Assert.Equal(SqlDbType.Int, topParam.SqlDbType);
        Assert.Equal(SqlDmvReader.MaxMissingIndexRows, topParam.Value);

        Assert.Contains("CASE WHEN @db IS NULL THEN DB_ID() ELSE DB_ID(@db) END", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("COALESCE(MAX(CAST(gs.avg_user_impact AS bigint)), 0)", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("DB_NAME(d.database_id) IS NOT NULL", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("OBJECT_SCHEMA_NAME(d.object_id, d.database_id) IS NOT NULL", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("OBJECT_NAME(d.object_id, d.database_id) IS NOT NULL", cmd.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingIndexes_TrimmedDbNameAndOverlongDbName_AreHandledSafely()
    {
        using var cmd = SqlDmvReader.CreateMissingIndexesCommand(null, "  SalesDb  ");
        Assert.NotNull(cmd);
        Assert.Equal("SalesDb", cmd!.Parameters["@db"].Value);

        var overlongDb = new string('a', SqlDmvReader.MaxSqlIdentifierLength + 1);
        Assert.Null(SqlDmvReader.CreateMissingIndexesCommand(null, overlongDb));

        using var reader = new SqlDmvReader("Server=127.0.0.1,1;Database=master;Connect Timeout=1;");
        Assert.Empty(reader.GetMissingIndexes(overlongDb));
        Assert.False(reader.IsConnected);
    }

    [Fact]
    public void CreateIndexUsageCommand_WithoutFilters_OmitsSchemaAndTableParamsAndHandlesHeaps()
    {
        using var cmd = SqlDmvReader.CreateIndexUsageCommand(null, null, "   ");

        Assert.NotNull(cmd);
        Assert.Equal(SqlDmvReader.DefaultCommandTimeoutSeconds, cmd!.CommandTimeout);
        var topParam = Assert.Single(cmd.Parameters.Cast<SqlParameter>());
        Assert.Equal("@top", topParam.ParameterName);
        Assert.Equal(SqlDbType.Int, topParam.SqlDbType);
        Assert.Equal(SqlDmvReader.MaxIndexUsageRows, topParam.Value);

        Assert.Contains("COALESCE(i.name, N'(HEAP)')", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("OBJECT_SCHEMA_NAME(ius.object_id) IS NOT NULL", cmd.CommandText, StringComparison.Ordinal);
        Assert.Contains("OBJECT_NAME(ius.object_id) IS NOT NULL", cmd.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("@schema", cmd.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("@table", cmd.CommandText, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateIndexUsageCommand_WithFilters_BindsExactParametersWithoutLikeWildcardInjection()
    {
        using var schemaOnly = SqlDmvReader.CreateIndexUsageCommand(null, " dbo ", null);
        Assert.NotNull(schemaOnly);
        Assert.Equal(2, schemaOnly!.Parameters.Count);
        Assert.Equal("dbo", schemaOnly.Parameters["@schema"].Value);
        Assert.Equal(SqlDbType.NVarChar, schemaOnly.Parameters["@schema"].SqlDbType);
        Assert.Equal(SqlDmvReader.MaxSqlIdentifierLength, schemaOnly.Parameters["@schema"].Size);
        Assert.Contains("OBJECT_SCHEMA_NAME(ius.object_id) = @schema", schemaOnly.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("@table", schemaOnly.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE @schema", schemaOnly.CommandText, StringComparison.OrdinalIgnoreCase);

        using var tableOnly = SqlDmvReader.CreateIndexUsageCommand(null, "  ", " Order_Items ");
        Assert.NotNull(tableOnly);
        Assert.Equal(2, tableOnly!.Parameters.Count);
        Assert.Equal("Order_Items", tableOnly.Parameters["@table"].Value);
        Assert.Equal(SqlDbType.NVarChar, tableOnly.Parameters["@table"].SqlDbType);
        Assert.Equal(SqlDmvReader.MaxSqlIdentifierLength, tableOnly.Parameters["@table"].Size);
        Assert.Contains("OBJECT_NAME(ius.object_id) = @table", tableOnly.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("@schema", tableOnly.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE @table", tableOnly.CommandText, StringComparison.OrdinalIgnoreCase);

        using var both = SqlDmvReader.CreateIndexUsageCommand(null, "dbo", "Order_Items");
        Assert.NotNull(both);
        Assert.Equal(3, both!.Parameters.Count);
        Assert.Equal("dbo", both.Parameters["@schema"].Value);
        Assert.Equal("Order_Items", both.Parameters["@table"].Value);
    }

    [Fact]
    public void IndexUsage_OverlongSchemaOrTable_ReturnsEmptyWithoutOpeningConnection()
    {
        var overlong = new string('x', SqlDmvReader.MaxSqlIdentifierLength + 1);
        Assert.Null(SqlDmvReader.CreateIndexUsageCommand(null, schema: overlong));
        Assert.Null(SqlDmvReader.CreateIndexUsageCommand(null, table: overlong));

        using var reader = new SqlDmvReader("Server=127.0.0.1,1;Database=master;Connect Timeout=1;");
        Assert.Empty(reader.GetIndexUsage(schema: overlong));
        Assert.Empty(reader.GetIndexUsage(table: overlong));
        Assert.False(reader.IsConnected);
    }

    [Fact]
    public void RowMappers_MapPopulatedAndDbNullColumnsSafely()
    {
        var topStat = SqlDmvReader.MapQueryExecutionStat(new FakeDataRecord("SELECT 1;", 10L, 250L, 400L, 9000L));
        Assert.Equal(new QueryExecutionStat("SELECT 1;", 10L, 250L, 400L, 9000L), topStat);

        var nullTopStat = SqlDmvReader.MapQueryExecutionStat(new FakeDataRecord(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value));
        Assert.Equal(new QueryExecutionStat(string.Empty, 0L, 0L, 0L, 0L), nullTopStat);

        var idxPopulated = SqlDmvReader.MapIndexUsage(new FakeDataRecord("dbo", "Orders", "IX_Orders_Customer", 50L, 2L, 1L, 5L));
        Assert.Equal(new IndexUsage("dbo", "Orders", "IX_Orders_Customer", 50L, 2L, 1L, 5L), idxPopulated);

        var idxHeap = SqlDmvReader.MapIndexUsage(new FakeDataRecord("dbo", "HeapLog", DBNull.Value, 0L, 12L, 0L, 3L));
        Assert.Equal(new IndexUsage("dbo", "HeapLog", "(HEAP)", 0L, 12L, 0L, 3L), idxHeap);

        var idxAllNull = SqlDmvReader.MapIndexUsage(new FakeDataRecord(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value));
        Assert.Equal(new IndexUsage(string.Empty, string.Empty, "(HEAP)", 0L, 0L, 0L, 0L), idxAllNull);

        var cachedStat = SqlDmvReader.MapCachedQueryStat(new FakeDataRecord("SELECT Id FROM dbo.T;", 15L, 300L, 120L, 5000L, 42));
        Assert.Equal(new CachedQueryStat("SELECT Id FROM dbo.T;", 15L, 300L, 120L, 5000L, 42), cachedStat);

        var nullCachedStat = SqlDmvReader.MapCachedQueryStat(new FakeDataRecord(DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value));
        Assert.Equal(new CachedQueryStat(string.Empty, 0L, 0L, 0L, 0L, 0), nullCachedStat);

        var missingPopulated = SqlDmvReader.MapMissingIndex(new FakeDataRecord("SalesDb", "dbo", "Orders", "[CustomerId]", "[OrderDate]", "[Total]", 120L, 8L, 92L));
        Assert.Equal(new MissingIndex("SalesDb", "dbo", "Orders", "[CustomerId]", "[OrderDate]", "[Total]", 120L, 8L, 92L), missingPopulated);

        var missingNullable = SqlDmvReader.MapMissingIndex(new FakeDataRecord("SalesDb", "dbo", "Orders", DBNull.Value, DBNull.Value, DBNull.Value, 10L, 2L, DBNull.Value));
        Assert.Equal(new MissingIndex("SalesDb", "dbo", "Orders", null, null, null, 10L, 2L, 0L), missingNullable);
    }
}
