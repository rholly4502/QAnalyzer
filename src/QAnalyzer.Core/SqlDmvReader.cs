using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace QAnalyzer.Core;

/// <summary>
/// Implementasi nyata via Microsoft.Data.SqlClient — baca DMV.
/// Hanya untuk koneksi lokal (WPF); bila tidak ada server hidup, jangan dipakai.
/// </summary>
public sealed class SqlDmvReader : ISqlMetadataReader
{
    internal const int DefaultCommandTimeoutSeconds = 15;
    public const int MaxTopQueryStats = 1000;
    internal const int MaxIndexUsageRows = 500;
    internal const int MaxMissingIndexRows = 100;
    internal const int MaxSqlIdentifierLength = 128;
    internal const string HeapIndexPlaceholder = "(HEAP)";

    internal const string CachedPlanQuerySql = @"
SELECT TOP 1
    tqp.query_plan
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
CROSS APPLY sys.dm_exec_text_query_plan(
    qs.plan_handle,
    qs.statement_start_offset,
    qs.statement_end_offset) tqp
WHERE st.text IS NOT NULL
  AND st.text LIKE @pattern ESCAPE '\'
  AND tqp.query_plan IS NOT NULL
  AND st.text NOT LIKE '%sys.dm_exec_text_query_plan%'
ORDER BY qs.total_elapsed_time DESC;";

    internal const string TopQueryStatsSql = @"
SELECT TOP (@top)
    SUBSTRING(
        st.text,
        (qs.statement_start_offset / 2) + 1,
        CASE
            WHEN qs.statement_end_offset = -1 OR qs.statement_end_offset < qs.statement_start_offset
                THEN (DATALENGTH(st.text) / 2) + 1
            ELSE ((qs.statement_end_offset - qs.statement_start_offset) / 2) + 1
        END
    ) AS sql_text,
    qs.execution_count,
    qs.total_worker_time / 1000 AS worker_ms,
    qs.total_elapsed_time / 1000 AS elapsed_ms,
    qs.total_logical_reads
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text IS NOT NULL
  AND st.text NOT LIKE '%sys.dm_exec_query_stats%'
ORDER BY qs.total_elapsed_time DESC;";

    internal const string StatsForSqlQuerySql = @"
SELECT TOP 1
    SUBSTRING(
        st.text,
        (qs.statement_start_offset / 2) + 1,
        CASE
            WHEN qs.statement_end_offset = -1 OR qs.statement_end_offset < qs.statement_start_offset
                THEN (DATALENGTH(st.text) / 2) + 1
            ELSE ((qs.statement_end_offset - qs.statement_start_offset) / 2) + 1
        END
    ) AS sql_text,
    qs.execution_count,
    qs.total_elapsed_time / 1000 AS elapsed_ms,
    qs.total_worker_time / 1000 AS worker_ms,
    qs.total_logical_reads,
    DATEDIFF(MINUTE, qs.creation_time, GETDATE()) AS plan_age_minutes
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text IS NOT NULL
  AND st.text LIKE @pattern ESCAPE '\'
  AND st.text NOT LIKE '%sys.dm_exec_query_stats%'
ORDER BY qs.total_elapsed_time DESC;";

    internal const string MissingIndexesQuerySql = @"
SELECT TOP (@top)
    DB_NAME(d.database_id) AS [db],
    OBJECT_SCHEMA_NAME(d.object_id, d.database_id) AS [schema],
    OBJECT_NAME(d.object_id, d.database_id) AS [table],
    MAX(d.equality_columns) AS equality_columns,
    MAX(d.inequality_columns) AS inequality_columns,
    MAX(d.included_columns) AS included_columns,
    SUM(gs.user_seeks) AS user_seeks,
    SUM(gs.user_scans) AS user_scans,
    COALESCE(MAX(CAST(gs.avg_user_impact AS bigint)), 0) AS avg_user_impact
FROM sys.dm_db_missing_index_details d
JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
JOIN sys.dm_db_missing_index_group_stats gs ON gs.group_handle = g.index_group_handle
WHERE d.database_id = CASE WHEN @db IS NULL THEN DB_ID() ELSE DB_ID(@db) END
  AND DB_NAME(d.database_id) IS NOT NULL
  AND OBJECT_SCHEMA_NAME(d.object_id, d.database_id) IS NOT NULL
  AND OBJECT_NAME(d.object_id, d.database_id) IS NOT NULL
GROUP BY d.database_id, d.object_id
ORDER BY SUM(gs.user_seeks) + SUM(gs.user_scans) DESC;";

    internal const string IndexUsageBaseSql = @"
SELECT TOP (@top)
    OBJECT_SCHEMA_NAME(ius.object_id) AS [schema],
    OBJECT_NAME(ius.object_id) AS [table],
    COALESCE(i.name, N'(HEAP)') AS [index],
    ius.user_seeks,
    ius.user_scans,
    ius.user_lookups,
    ius.user_updates
FROM sys.dm_db_index_usage_stats ius
JOIN sys.indexes i ON i.object_id = ius.object_id AND i.index_id = ius.index_id
WHERE ius.database_id = DB_ID()
  AND OBJECT_SCHEMA_NAME(ius.object_id) IS NOT NULL
  AND OBJECT_NAME(ius.object_id) IS NOT NULL";

    internal const string ShowPlanOnSql = "SET SHOWPLAN_XML ON;";
    internal const string ShowPlanOffSql = "SET SHOWPLAN_XML OFF;";

    private readonly SqlConnection _conn;

    public SqlDmvReader(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _conn = new SqlConnection(connectionString);
    }

    public bool IsConnected => _conn.State == ConnectionState.Open;

    public void Open() => _conn.Open();

    public Task OpenAsync(CancellationToken ct = default) => _conn.OpenAsync(ct);

    public IReadOnlyList<QueryExecutionStat> GetTopQueryStats(int top)
    {
        using var cmd = CreateTopQueryStatsCommand(_conn, top);
        using var r = cmd.ExecuteReader();
        var list = new List<QueryExecutionStat>();
        while (r.Read())
        {
            list.Add(MapQueryExecutionStat(r));
        }
        return list;
    }

    public IReadOnlyList<IndexUsage> GetIndexUsage(string? schema = null, string? table = null)
    {
        using var cmd = CreateIndexUsageCommand(_conn, schema, table);
        if (cmd is null)
        {
            return [];
        }

        using var r = cmd.ExecuteReader();
        var list = new List<IndexUsage>();
        while (r.Read())
        {
            list.Add(MapIndexUsage(r));
        }
        return list;
    }

    /// <summary>
    /// Mencari statistik historis untuk query yang cocok (parameterized/plan cache)
    /// tanpa mengeksekusi. Cocokkan via normalisasi literal aman + cari di plan cache.
    /// </summary>
    public CachedQueryStat? GetStatsForSql(string sqlText)
    {
        using var cmd = CreateStatsForSqlCommand(_conn, sqlText);
        if (cmd is null)
        {
            return null;
        }

        using var r = cmd.ExecuteReader();
        return r.Read() ? MapCachedQueryStat(r) : null;
    }

    /// <summary>
    /// Missing index dari sys.dm_db_missing_index_details, diagregasi per tabel.
    /// Ini menangkap kasus "query tidak pakai index karena index-nya belum ada".
    /// </summary>
    public IReadOnlyList<MissingIndex> GetMissingIndexes(string? dbName = null)
    {
        using var cmd = CreateMissingIndexesCommand(_conn, dbName);
        if (cmd is null)
        {
            return [];
        }

        using var r = cmd.ExecuteReader();
        var list = new List<MissingIndex>();
        while (r.Read())
        {
            list.Add(MapMissingIndex(r));
        }
        return list;
    }

    internal static SqlCommand CreateTopQueryStatsCommand(SqlConnection? conn, int top)
    {
        if (top <= 0 || top > MaxTopQueryStats)
        {
            throw new ArgumentOutOfRangeException(
                nameof(top),
                top,
                $"Nilai top harus berada di antara 1 dan {MaxTopQueryStats}.");
        }

        var cmd = CreateReadOnlyCommand(TopQueryStatsSql, conn);
        AddIntParameter(cmd, "@top", top);
        return cmd;
    }

    internal static SqlCommand? CreateIndexUsageCommand(SqlConnection? conn, string? schema = null, string? table = null)
    {
        if (!TryNormalizeIdentifier(schema, out var normalizedSchema)
            || !TryNormalizeIdentifier(table, out var normalizedTable))
        {
            return null;
        }

        var sql = new StringBuilder(IndexUsageBaseSql);
        if (normalizedSchema is not null)
        {
            sql.Append("\n  AND OBJECT_SCHEMA_NAME(ius.object_id) = @schema");
        }

        if (normalizedTable is not null)
        {
            sql.Append("\n  AND OBJECT_NAME(ius.object_id) = @table");
        }

        sql.Append("\nORDER BY (ius.user_seeks + ius.user_scans + ius.user_lookups) DESC;");

        var cmd = CreateReadOnlyCommand(sql.ToString(), conn);
        AddIntParameter(cmd, "@top", MaxIndexUsageRows);
        if (normalizedSchema is not null)
        {
            AddNVarCharParameter(cmd, "@schema", MaxSqlIdentifierLength, normalizedSchema);
        }

        if (normalizedTable is not null)
        {
            AddNVarCharParameter(cmd, "@table", MaxSqlIdentifierLength, normalizedTable);
        }

        return cmd;
    }

    internal static SqlCommand? CreateStatsForSqlCommand(SqlConnection? conn, string sqlText)
    {
        ArgumentNullException.ThrowIfNull(sqlText);

        var pattern = ExecutionPlanQueryGuard.BuildPlanCacheLikePattern(sqlText);
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        var cmd = CreateReadOnlyCommand(StatsForSqlQuerySql, conn);
        AddNVarCharParameter(cmd, "@pattern", 4000, pattern);
        return cmd;
    }

    internal static SqlCommand? CreateMissingIndexesCommand(SqlConnection? conn, string? dbName = null)
    {
        if (!TryNormalizeIdentifier(dbName, out var normalizedDb))
        {
            return null;
        }

        var cmd = CreateReadOnlyCommand(MissingIndexesQuerySql, conn);
        AddIntParameter(cmd, "@top", MaxMissingIndexRows);
        AddNVarCharParameter(cmd, "@db", MaxSqlIdentifierLength, (object?)normalizedDb ?? DBNull.Value);
        return cmd;
    }

    internal static QueryExecutionStat MapQueryExecutionStat(IDataRecord r) =>
        new(
            GetSafeString(r, 0),
            GetSafeInt64(r, 1),
            GetSafeInt64(r, 2),
            GetSafeInt64(r, 3),
            GetSafeInt64(r, 4));

    internal static IndexUsage MapIndexUsage(IDataRecord r) =>
        new(
            GetSafeString(r, 0),
            GetSafeString(r, 1),
            GetSafeString(r, 2, HeapIndexPlaceholder),
            GetSafeInt64(r, 3),
            GetSafeInt64(r, 4),
            GetSafeInt64(r, 5),
            GetSafeInt64(r, 6));

    internal static CachedQueryStat MapCachedQueryStat(IDataRecord r) =>
        new(
            GetSafeString(r, 0),
            GetSafeInt64(r, 1),
            GetSafeInt64(r, 2),
            GetSafeInt64(r, 3),
            GetSafeInt64(r, 4),
            GetSafeInt32(r, 5));

    internal static MissingIndex MapMissingIndex(IDataRecord r) =>
        new(
            GetSafeString(r, 0),
            GetSafeString(r, 1),
            GetSafeString(r, 2),
            GetSafeNullableString(r, 3),
            GetSafeNullableString(r, 4),
            GetSafeNullableString(r, 5),
            GetSafeInt64(r, 6),
            GetSafeInt64(r, 7),
            GetSafeInt64(r, 8));

    private static SqlCommand CreateReadOnlyCommand(string sql, SqlConnection? conn) =>
        new(sql, conn) { CommandTimeout = DefaultCommandTimeoutSeconds };

    private static void AddIntParameter(SqlCommand cmd, string name, int value) =>
        cmd.Parameters.Add(name, SqlDbType.Int).Value = value;

    private static void AddNVarCharParameter(SqlCommand cmd, string name, int size, object value) =>
        cmd.Parameters.Add(name, SqlDbType.NVarChar, size).Value = value;

    private static bool TryNormalizeIdentifier(string? raw, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            normalized = null;
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length > MaxSqlIdentifierLength)
        {
            normalized = null;
            return false;
        }

        normalized = trimmed;
        return true;
    }

    private static string GetSafeString(IDataRecord r, int ordinal, string fallback = "") =>
        r.IsDBNull(ordinal) ? fallback : r.GetString(ordinal);

    private static string? GetSafeNullableString(IDataRecord r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static long GetSafeInt64(IDataRecord r, int ordinal) =>
        r.IsDBNull(ordinal) ? 0L : r.GetInt64(ordinal);

    private static int GetSafeInt32(IDataRecord r, int ordinal) =>
        r.IsDBNull(ordinal) ? 0 : r.GetInt32(ordinal);

    /// <summary>
    /// Mengambil XML Execution Plan lokal tanpa mengeksekusi mutasi data:
    /// cek plan cache terlebih dahulu via <c>sys.dm_exec_text_query_plan</c>,
    /// lalu fallback ke <c>SET SHOWPLAN_XML ON</c> hanya jika lolos guard AST read-only.
    /// </summary>
    public async Task<ExecutionPlanResult?> GetExecutionPlanXmlAsync(string sqlText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sqlText);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(sqlText)) return null;

        if (!IsConnected) await OpenAsync(ct);

        return await TryGetCachedExecutionPlanAsync(sqlText, ct)
            ?? await TryGetEstimatedExecutionPlanAsync(sqlText, ct);
    }

    private async Task<ExecutionPlanResult?> TryGetCachedExecutionPlanAsync(string sqlText, CancellationToken ct)
    {
        var pattern = ExecutionPlanQueryGuard.BuildPlanCacheLikePattern(sqlText);
        if (string.IsNullOrEmpty(pattern)) return null;

        try
        {
            await using var cmd = new SqlCommand(CachedPlanQuerySql, _conn) { CommandTimeout = DefaultCommandTimeoutSeconds };
            cmd.Parameters.Add("@pattern", System.Data.SqlDbType.NVarChar, 4000).Value = pattern;
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is string xml && !string.IsNullOrWhiteSpace(xml)
                ? new ExecutionPlanResult(xml, ExecutionPlanSource.PlanCache)
                : null;
        }
        catch (SqlException ex) when (ex.Number is 297 or 300)
        {
            // Tidak memiliki izin VIEW SERVER STATE — lanjutkan ke estimated plan (SHOWPLAN_XML).
            return null;
        }
    }

    private async Task<ExecutionPlanResult?> TryGetEstimatedExecutionPlanAsync(string sqlText, CancellationToken ct)
    {
        if (!ExecutionPlanQueryGuard.CanSafelyEstimatePlan(sqlText, out _)) return null;

        await using (var onCmd = new SqlCommand(ShowPlanOnSql, _conn) { CommandTimeout = DefaultCommandTimeoutSeconds })
            await onCmd.ExecuteNonQueryAsync(ct);

        try
        {
            await using var planCmd = new SqlCommand(sqlText, _conn) { CommandTimeout = DefaultCommandTimeoutSeconds };
            await using var reader = await planCmd.ExecuteReaderAsync(ct);
            do
            {
                while (await reader.ReadAsync(ct))
                {
                    var xml = reader.FieldCount > 0 && !await reader.IsDBNullAsync(0, ct)
                        ? reader.GetValue(0)?.ToString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(xml))
                        return new ExecutionPlanResult(xml, ExecutionPlanSource.EstimatedShowPlan);
                }
            } while (await reader.NextResultAsync(ct));

            return null;
        }
        finally
        {
            try
            {
                if (_conn.State == System.Data.ConnectionState.Open)
                {
                    await using var offCmd = new SqlCommand(ShowPlanOffSql, _conn) { CommandTimeout = DefaultCommandTimeoutSeconds };
                    await offCmd.ExecuteNonQueryAsync(CancellationToken.None);
                }
            }
            catch
            {
                _conn.Close();
            }
        }
    }

    public void Dispose() => _conn.Dispose();
}
