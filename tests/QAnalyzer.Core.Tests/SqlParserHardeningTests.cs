using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Core.Tests;

public sealed class SqlParserHardeningTests
{
    private sealed class RecordingMetadataReader : ISqlMetadataReader
    {
        public bool IsConnected => true;
        public string? LastSql { get; private set; }

        public IReadOnlyList<QueryExecutionStat> GetTopQueryStats(int top) => [];
        public IReadOnlyList<IndexUsage> GetIndexUsage(string? schema = null, string? table = null) => [];
        public CachedQueryStat? GetStatsForSql(string sqlText) => null;
        public IReadOnlyList<MissingIndex> GetMissingIndexes(string? dbName = null) => [];

        public Task<ExecutionPlanResult?> GetExecutionPlanXmlAsync(string sqlText, CancellationToken ct = default)
        {
            LastSql = sqlText;
            return Task.FromResult<ExecutionPlanResult?>(null);
        }

        public void Dispose() { }
    }

    private sealed class CustomCriticalRule(int count, string ruleId = "CUSTOM_CRIT") : ISqlRule
    {
        public string Id => ruleId;
        public string Title => "Custom critical";
        public RiskLevel Level => RiskLevel.Critical;

        public IEnumerable<Diagnostic> Evaluate(string sql) =>
            Enumerable.Range(1, count).Select(i => new Diagnostic(Level, Id, Title, $"Crit {i}", Line: i));
    }

    private static SqlAnalyzer CreateDefaultAnalyzer() => new(SqlAnalyzer.CreateDefaultRules());

    private static void RunOnSmallStack(Action action, int stackBytes = 1024 * 1024)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        }, stackBytes);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "Worker thread did not complete in time.");
        if (captured is not null)
        {
            throw captured;
        }
    }

    [Fact]
    public void Analyze_ParenthesesAtAndAboveMaxNestingDepth_EnforcesLimit()
    {
        var analyzer = CreateDefaultAnalyzer();
        var atLimit = "SELECT " + new string('(', SqlAnalyzer.MaxNestingDepth) + "1" + new string(')', SqlAnalyzer.MaxNestingDepth) + ";";
        var overLimit = "SELECT " + new string('(', SqlAnalyzer.MaxNestingDepth + 1) + "1" + new string(')', SqlAnalyzer.MaxNestingDepth + 1) + ";";

        var okReport = analyzer.Analyze(atLimit);
        Assert.False(okReport.HasCritical);

        var ex = Assert.Throws<FormatException>(() => analyzer.Analyze(overLimit));
        Assert.Contains(SqlAnalyzer.MaxNestingDepth.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_BeginEndAndTryCatchAtAndAboveMaxNestingDepth_EnforcesLimit()
    {
        var analyzer = CreateDefaultAnalyzer();

        var beginAtLimit = string.Concat(Enumerable.Repeat("BEGIN ", SqlAnalyzer.MaxNestingDepth))
            + "SELECT 1;"
            + string.Concat(Enumerable.Repeat(" END", SqlAnalyzer.MaxNestingDepth));
        var beginOverLimit = string.Concat(Enumerable.Repeat("BEGIN ", SqlAnalyzer.MaxNestingDepth + 1))
            + "SELECT 1;"
            + string.Concat(Enumerable.Repeat(" END", SqlAnalyzer.MaxNestingDepth + 1));

        Assert.False(analyzer.Analyze(beginAtLimit).HasCritical);
        var beginEx = Assert.Throws<FormatException>(() => analyzer.Analyze(beginOverLimit));
        Assert.Contains(SqlAnalyzer.MaxNestingDepth.ToString(), beginEx.Message, StringComparison.Ordinal);

        var tryAtLimit = string.Concat(Enumerable.Repeat("BEGIN TRY ", SqlAnalyzer.MaxNestingDepth))
            + "SELECT 1;"
            + string.Concat(Enumerable.Repeat(" END TRY BEGIN CATCH SELECT 1; END CATCH", SqlAnalyzer.MaxNestingDepth));
        var tryOverLimit = string.Concat(Enumerable.Repeat("BEGIN TRY ", SqlAnalyzer.MaxNestingDepth + 1))
            + "SELECT 1;"
            + string.Concat(Enumerable.Repeat(" END TRY BEGIN CATCH SELECT 1; END CATCH", SqlAnalyzer.MaxNestingDepth + 1));

        Assert.False(analyzer.Analyze(tryAtLimit).HasCritical);
        Assert.Throws<FormatException>(() => analyzer.Analyze(tryOverLimit));
    }

    [Fact]
    public void Analyze_CaseNestingAtAndAboveMaxNestingDepth_EnforcesLimit()
    {
        var analyzer = CreateDefaultAnalyzer();
        var atLimit = "SELECT "
            + string.Concat(Enumerable.Repeat("CASE WHEN 1=1 THEN ", SqlAnalyzer.MaxNestingDepth))
            + "1"
            + string.Concat(Enumerable.Repeat(" ELSE 0 END", SqlAnalyzer.MaxNestingDepth))
            + ";";
        var overLimit = "SELECT "
            + string.Concat(Enumerable.Repeat("CASE WHEN 1=1 THEN ", SqlAnalyzer.MaxNestingDepth + 1))
            + "1"
            + string.Concat(Enumerable.Repeat(" ELSE 0 END", SqlAnalyzer.MaxNestingDepth + 1))
            + ";";

        Assert.False(analyzer.Analyze(atLimit).HasCritical);
        var ex = Assert.Throws<FormatException>(() => analyzer.Analyze(overLimit));
        Assert.Contains(SqlAnalyzer.MaxNestingDepth.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ControlFlowNestingIfWhileAndElseIf_EnforcesLimit()
    {
        var analyzer = CreateDefaultAnalyzer();

        var ifAtLimit = string.Concat(Enumerable.Repeat("IF 1=1 ", SqlAnalyzer.MaxNestingDepth)) + "SELECT 1;";
        var ifOverLimit = string.Concat(Enumerable.Repeat("IF 1=1 ", SqlAnalyzer.MaxNestingDepth + 1)) + "SELECT 1;";
        Assert.False(analyzer.Analyze(ifAtLimit).HasCritical);
        Assert.Throws<FormatException>(() => analyzer.Analyze(ifOverLimit));

        var whileAtLimit = string.Concat(Enumerable.Repeat("WHILE 1=1 ", SqlAnalyzer.MaxNestingDepth)) + "SELECT 1;";
        var whileOverLimit = string.Concat(Enumerable.Repeat("WHILE 1=1 ", SqlAnalyzer.MaxNestingDepth + 1)) + "SELECT 1;";
        Assert.False(analyzer.Analyze(whileAtLimit).HasCritical);
        Assert.Throws<FormatException>(() => analyzer.Analyze(whileOverLimit));

        var elseIfAtLimit = "IF @x = 0 SET @y = 0;\n"
            + string.Join("\n", Enumerable.Range(1, SqlAnalyzer.MaxNestingDepth - 1).Select(i => $"ELSE IF @x = {i} SET @y = {i};"));
        var elseIfOverLimit = "IF @x = 0 SET @y = 0;\n"
            + string.Join("\n", Enumerable.Range(1, SqlAnalyzer.MaxNestingDepth).Select(i => $"ELSE IF @x = {i} SET @y = {i};"));
        Assert.False(analyzer.Analyze(elseIfAtLimit).HasCritical);
        Assert.Throws<FormatException>(() => analyzer.Analyze(elseIfOverLimit));

        var updateInsideElseIfOverLimit = "IF @x = 0 UPDATE dbo.T SET a = 0 WHERE Id = 0\n"
            + string.Join("\n", Enumerable.Range(1, SqlAnalyzer.MaxNestingDepth).Select(i => $"ELSE IF @x = {i} UPDATE dbo.T SET a = {i} WHERE Id = {i}"));
        Assert.Throws<FormatException>(() => analyzer.Analyze(updateInsideElseIfOverLimit));
    }

    [Fact]
    public void Analyze_MixedBeginAndParentheses_TracksCombinedNestingDepth()
    {
        var analyzer = CreateDefaultAnalyzer();
        const int beginLevels = 65;
        var parenAtLimit = SqlAnalyzer.MaxNestingDepth - beginLevels; // 63 -> total 128
        var parenOverLimit = parenAtLimit + 1; // 64 -> total 129

        var atLimit = string.Concat(Enumerable.Repeat("BEGIN ", beginLevels))
            + "SELECT " + new string('(', parenAtLimit) + "1" + new string(')', parenAtLimit) + ";"
            + string.Concat(Enumerable.Repeat(" END", beginLevels));
        var overLimit = string.Concat(Enumerable.Repeat("BEGIN ", beginLevels))
            + "SELECT " + new string('(', parenOverLimit) + "1" + new string(')', parenOverLimit) + ";"
            + string.Concat(Enumerable.Repeat(" END", beginLevels));

        Assert.False(analyzer.Analyze(atLimit).HasCritical);
        Assert.Throws<FormatException>(() => analyzer.Analyze(overLimit));
    }

    [Theory]
    [InlineData("NOT ", "SELECT 1 WHERE {0} 1=1;")]
    [InlineData("+", "SELECT {0}1;")]
    [InlineData("- ", "SELECT {0}1;")]
    [InlineData("~", "SELECT {0}1;")]
    public void Analyze_ConsecutiveUnaryOperatorsAtAndAboveLimit_EnforcesLimit(string op, string template)
    {
        var analyzer = CreateDefaultAnalyzer();
        var atLimit = string.Format(template, string.Concat(Enumerable.Repeat(op, SqlAnalyzer.MaxConsecutiveUnaryOperators)));
        var overLimit = string.Format(template, string.Concat(Enumerable.Repeat(op, SqlAnalyzer.MaxConsecutiveUnaryOperators + 1)));

        Assert.False(analyzer.Analyze(atLimit).HasCritical);
        var ex = Assert.Throws<FormatException>(() => analyzer.Analyze(overLimit));
        Assert.Contains(SqlAnalyzer.MaxConsecutiveUnaryOperators.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyze_ChainedOperatorsPerStatementAtAndAboveLimit_EnforcesLimit()
    {
        var analyzer = CreateDefaultAnalyzer();
        var limit = SqlAnalyzer.MaxChainedOperatorsPerStatement;

        var cases = new (string Ok, string Over)[]
        {
            (
                "SELECT 1 FROM dbo.T WHERE a = 0" + string.Concat(Enumerable.Repeat(" AND a = 1", limit)) + ";",
                "SELECT 1 FROM dbo.T WHERE a = 0" + string.Concat(Enumerable.Repeat(" AND a = 1", limit + 1)) + ";"
            ),
            (
                "SELECT 1" + string.Concat(Enumerable.Repeat("+1", limit)) + ";",
                "SELECT 1" + string.Concat(Enumerable.Repeat("+1", limit + 1)) + ";"
            ),
            (
                "SELECT 1" + string.Concat(Enumerable.Repeat(" UNION SELECT 1", limit)) + ";",
                "SELECT 1" + string.Concat(Enumerable.Repeat(" UNION SELECT 1", limit + 1)) + ";"
            ),
            (
                "SELECT 1" + string.Concat(Enumerable.Repeat(" UNION ALL SELECT 1", limit)) + ";",
                "SELECT 1" + string.Concat(Enumerable.Repeat(" UNION ALL SELECT 1", limit + 1)) + ";"
            ),
            (
                "SELECT t0.Id FROM dbo.T0 t0 " + string.Join(" ", Enumerable.Range(1, limit).Select(i => $"JOIN dbo.T{i} t{i} ON t0.Id = t{i}.Id")) + ";",
                "SELECT t0.Id FROM dbo.T0 t0 " + string.Join(" ", Enumerable.Range(1, limit + 1).Select(i => $"JOIN dbo.T{i} t{i} ON t0.Id = t{i}.Id")) + ";"
            ),
            (
                "SELECT t0.Id FROM dbo.T0 t0 " + string.Join(" ", Enumerable.Range(1, limit).Select(i => $"CROSS APPLY (SELECT {i} AS x) a{i}")) + ";",
                "SELECT t0.Id FROM dbo.T0 t0 " + string.Join(" ", Enumerable.Range(1, limit + 1).Select(i => $"CROSS APPLY (SELECT {i} AS x) a{i}")) + ";"
            ),
            (
                "SELECT t0.Id FROM dbo.T0 t0, " + string.Join(", ", Enumerable.Range(1, limit).Select(i => $"dbo.T{i} t{i}")) + ";",
                "SELECT t0.Id FROM dbo.T0 t0, " + string.Join(", ", Enumerable.Range(1, limit + 1).Select(i => $"dbo.T{i} t{i}")) + ";"
            ),
        };

        foreach (var (okSql, overSql) in cases)
        {
            Assert.False(analyzer.Analyze(okSql).HasCritical);
            var ex = Assert.Throws<FormatException>(() => analyzer.Analyze(overSql));
            Assert.Contains(limit.ToString(), ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Analyze_AdversarialDeepPayloadsOnOneMbStack_ThrowFormatExceptionWithoutCrashing()
    {
        var analyzer = CreateDefaultAnalyzer();
        var payloads = new[]
        {
            "SELECT " + new string('(', 5000) + "1" + new string(')', 5000) + ";",
            string.Concat(Enumerable.Repeat("BEGIN ", 8000)) + "SELECT 1;" + string.Concat(Enumerable.Repeat(" END", 8000)),
            "SELECT 1 FROM dbo.T WHERE a = 0" + string.Concat(Enumerable.Repeat(" AND a = 1", 10000)) + ";",
            "SELECT 1 WHERE " + string.Concat(Enumerable.Repeat("NOT ", 5000)) + "1=1;",
        };

        foreach (var payload in payloads)
        {
            RunOnSmallStack(() => Assert.Throws<FormatException>(() => analyzer.Analyze(payload)));
        }
    }

    [Fact]
    public void Analyze_LegitimateComplexScripts_DoNotTriggerFalsePositives()
    {
        var analyzer = CreateDefaultAnalyzer();

        var sequentialBeginTran = string.Concat(Enumerable.Repeat("BEGIN TRAN; SELECT Id FROM dbo.T; COMMIT TRAN;\n", 150))
            + "BEGIN DIALOG CONVERSATION @h FROM SERVICE s TO SERVICE 't';";
        Assert.False(analyzer.Analyze(sequentialBeginTran).HasCritical);

        var flatCase = "SELECT CASE "
            + string.Join(" ", Enumerable.Range(1, 200).Select(i => $"WHEN @x = {i} THEN {i}"))
            + " ELSE 0 END FROM dbo.T;";
        Assert.False(analyzer.Analyze(flatCase).HasCritical);

        var sequentialIfSet = string.Concat(Enumerable.Range(1, 200).Select(i => $"IF @x = {i} SET @y = {i};\n"));
        Assert.False(analyzer.Analyze(sequentialIfSet).HasCritical);

        var sequentialIfBegin = string.Concat(Enumerable.Range(1, 200).Select(i => $"IF @x = {i} BEGIN SET @y = {i}; END\n"));
        Assert.False(analyzer.Analyze(sequentialIfBegin).HasCritical);

        var sequentialIfInsideBegin = "BEGIN\n"
            + string.Concat(Enumerable.Range(1, 200).Select(i => $"    IF @x = {i} SET @y = {i};\n"))
            + "END;";
        Assert.False(analyzer.Analyze(sequentialIfInsideBegin).HasCritical);

        var bigInList = "SELECT Id FROM dbo.Orders WHERE Id IN (" + string.Join(", ", Enumerable.Range(1, 2000)) + ");";
        Assert.False(analyzer.Analyze(bigInList).HasCritical);

        var manySelectColumns = "SELECT " + string.Join(", ", Enumerable.Range(1, 500).Select(i => $"c{i}")) + " FROM dbo.WideTable;";
        Assert.False(analyzer.Analyze(manySelectColumns).HasCritical);

        var multiStatementAnds = string.Concat(Enumerable.Range(1, 200).Select(s =>
            $"SELECT Id FROM dbo.T{s} WHERE " + string.Join(" AND ", Enumerable.Range(1, 10).Select(c => $"c{c} = 1")) + ";\n"));
        Assert.False(analyzer.Analyze(multiStatementAnds).HasCritical);

        var deepStringsAndComments = string.Join("\n",
            $"-- {new string('(', 5000)} {new string(')', 5000)}",
            $"/* {new string('(', 5000)} {new string(')', 5000)} */",
            $"SELECT '{new string('(', 5000)}{new string(')', 5000)}' AS txt FROM dbo.Orders;");
        var literalReport = analyzer.Analyze(deepStringsAndComments);
        Assert.Empty(literalReport.Diagnostics);
    }

    [Fact]
    public void Analyze_ExcessiveDiagnostics_CapsAtMaxDiagnosticsAndPrioritizesCritical()
    {
        var analyzer = CreateDefaultAnalyzer();
        var floodedSql = string.Concat(Enumerable.Repeat("SELECT * FROM dbo.Orders;\n", SqlAnalyzer.MaxDiagnostics + 50))
            + "DELETE FROM dbo.Orders;";

        var report = analyzer.Analyze(floodedSql);

        Assert.Equal(SqlAnalyzer.MaxDiagnostics, report.Total);
        Assert.True(report.HasCritical);
        var deleteDiag = Assert.Single(report.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE");
        Assert.Equal(RiskLevel.Critical, deleteDiag.Level);
        Assert.Equal(report.Diagnostics[^1], deleteDiag);
    }

    [Fact]
    public void Analyze_NormalScriptWithinLimit_PreservesExactCountAndOrder()
    {
        var analyzer = CreateDefaultAnalyzer();
        const string sql = """
            SELECT Id FROM dbo.Users WITH (NOLOCK);
            SELECT * FROM dbo.Orders;
            DELETE FROM dbo.Audit;
            """;

        var report = analyzer.Analyze(sql);

        Assert.Equal(3, report.Total);
        Assert.Equal(["SELECT_STAR", "NOLOCK", "DELETE_NO_WHERE"], report.Diagnostics.Select(d => d.RuleId).ToArray());
    }

    [Fact]
    public void Analyze_AstRuleAfterFloodedNonAstRule_StillEnforcesAstComplexityLimit()
    {
        var analyzer = new SqlAnalyzer([new CustomCriticalRule(SqlAnalyzer.MaxDiagnostics), new SelectStarRule()]);
        var overDeepSql = "SELECT " + new string('(', SqlAnalyzer.MaxNestingDepth + 1) + "1" + new string(')', SqlAnalyzer.MaxNestingDepth + 1) + ";";

        Assert.Throws<FormatException>(() => analyzer.Analyze(overDeepSql));
    }

    [Fact]
    public async Task NonThrowingAndLocalPlanCallers_HandleOverDeepSqlSafely()
    {
        var overDeepSql = "SELECT " + new string('(', SqlAnalyzer.MaxNestingDepth + 1) + "1" + new string(')', SqlAnalyzer.MaxNestingDepth + 1) + ";";

        Assert.Equal(overDeepSql, MaxDopTransformer.AddMaxDopOne(overDeepSql));

        var allowed = ExecutionPlanQueryGuard.CanSafelyEstimatePlan(overDeepSql, out var reason);
        Assert.False(allowed);
        Assert.False(string.IsNullOrWhiteSpace(reason));

        var reader = new RecordingMetadataReader();
        var localAnalyzer = new LocalExecutionPlanAnalyzer(SqlAnalyzer.CreateDefaultRules());
        await Assert.ThrowsAsync<FormatException>(() => localAnalyzer.AnalyzeAsync(overDeepSql, reader));
        Assert.Null(reader.LastSql);
    }
}