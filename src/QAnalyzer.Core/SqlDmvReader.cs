using Microsoft.Data.SqlClient;

namespace QAnalyzer.Core;

/// <summary>
/// Implementasi nyata via Microsoft.Data.SqlClient — baca DMV.
/// Hanya untuk koneksi lokal (WPF); bila tidak ada server hidup, jangan dipakai.
/// </summary>
public sealed class SqlDmvReader : ISqlMetadataReader
{
    private const int DefaultCommandTimeoutSeconds = 15;

    internal const string CachedPlanQuerySql = @"
SELECT TOP 1
    tqp.query_plan
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
CROSS APPLY sys.dm_exec_text_query_plan(
    qs.plan_handle,
    qs.statement_start_offset,
    qs.statement_end_offset) tqp
WHERE st.text LIKE @pattern ESCAPE '\'
  AND tqp.query_plan IS NOT NULL
  AND st.text NOT LIKE '%sys.dm_exec_text_query_plan%'
ORDER BY qs.total_elapsed_time DESC;";

    internal const string ShowPlanOnSql = "SET SHOWPLAN_XML ON;";
    internal const string ShowPlanOffSql = "SET SHOWPLAN_XML OFF;";

    private readonly SqlConnection _conn;

    public SqlDmvReader(string connectionString)
    {
        _conn = new SqlConnection(connectionString);
    }

    public bool IsConnected => _conn.State == System.Data.ConnectionState.Open;

    public void Open() => _conn.Open();

    public Task OpenAsync(CancellationToken ct = default) => _conn.OpenAsync(ct);

    public IReadOnlyList<QueryExecutionStat> GetTopQueryStats(int top)
    {
        const string sql = @"
SELECT TOP (@top)
    SUBSTRING(st.text, (qs.statement_start_offset/2)+1,
        ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
          ELSE qs.statement_end_offset END - qs.statement_start_offset)/2)+1) AS sql_text,
    qs.execution_count,
    qs.total_worker_time/1000 AS worker_ms,
    qs.total_elapsed_time/1000 AS elapsed_ms,
    qs.total_logical_reads
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
ORDER BY qs.total_elapsed_time DESC;";
        var list = new List<QueryExecutionStat>();
        using var cmd = new SqlCommand(sql, _conn);
        cmd.Parameters.AddWithValue("@top", top);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new QueryExecutionStat(
                r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        return list;
    }

    public IReadOnlyList<IndexUsage> GetIndexUsage(string? schema = null, string? table = null)
    {
        var sql = @"
SELECT OBJECT_SCHEMA_NAME(ius.object_id) AS [schema],
       OBJECT_NAME(ius.object_id) AS [table],
       i.name AS [index],
       ius.user_seeks, ius.user_scans, ius.user_lookups, ius.user_updates
FROM sys.dm_db_index_usage_stats ius
JOIN sys.indexes i ON i.object_id = ius.object_id AND i.index_id = ius.index_id
WHERE ius.database_id = DB_ID()";
        if (!string.IsNullOrWhiteSpace(schema) || !string.IsNullOrWhiteSpace(table))
            sql += " AND OBJECT_SCHEMA_NAME(ius.object_id) LIKE @schema AND OBJECT_NAME(ius.object_id) LIKE @table";
        var list = new List<IndexUsage>();
        using var cmd = new SqlCommand(sql, _conn);
        cmd.Parameters.AddWithValue("@schema", schema ?? "%");
        cmd.Parameters.AddWithValue("@table", table ?? "%");
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new IndexUsage(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
        return list;
    }


    /// <summary>
    /// Mencari statistik historis untuk query yang cocok (parameterized/plan cache)
    /// tanpa mengeksekusi. Cocokkan via hashing teks biasa + cari di plan cache.
    /// </summary>
    public CachedQueryStat? GetStatsForSql(string sqlText)
    {
        // parameterisasi sederhana: hapus literal string/angka supaya bisa cocok
        // dgn plan yang sudah di-parameterize oleh server.
        var normalized = System.Text.RegularExpressions.Regex.Replace(
            sqlText, @"'[^']*'|\b\d+\b", "?") .Trim();
        const string sql = @"
SELECT TOP 1
    SUBSTRING(st.text, (qs.statement_start_offset/2)+1,
        ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
          ELSE qs.statement_end_offset END - qs.statement_start_offset)/2)+1) AS sql_text,
    qs.execution_count,
    qs.total_elapsed_time/1000,
    qs.total_worker_time/1000,
    qs.total_logical_reads,
    DATEDIFF(MINUTE, cp.creation_time, GETDATE())
FROM sys.dm_exec_query_stats qs
JOIN sys.dm_exec_cached_plans cp ON cp.plan_handle = qs.plan_handle
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE st.text LIKE @pattern
ORDER BY qs.total_elapsed_time DESC;
";
        using var cmd = new SqlCommand(sql, _conn);
        cmd.Parameters.AddWithValue("@pattern", "%" + normalized.Substring(0, Math.Min(300, normalized.Length)) + "%");
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new CachedQueryStat(
            r.GetString(0), r.GetInt64(1), r.GetInt64(2),
            r.GetInt64(3), r.GetInt64(4), r.GetInt32(5));
    }


    /// <summary>
    /// Missing index dari sys.dm_db_missing_index_details, diagregasi per tabel.
    /// Ini menangkap kasus "query tidak pakai index karena index-nya belum ada".
    /// </summary>
    public IReadOnlyList<MissingIndex> GetMissingIndexes(string? dbName = null)
    {
        var sql = @"
SELECT
    DB_NAME(d.database_id) AS [db],
    OBJECT_SCHEMA_NAME(d.object_id, d.database_id) AS [schema],
    OBJECT_NAME(d.object_id, d.database_id) AS [table],
    MAX(d.equality_columns),
    MAX(d.inequality_columns),
    MAX(d.included_columns),
    SUM(gs.user_seeks),
    SUM(gs.user_scans),
    MAX(CAST(gs.avg_user_impact AS bigint))
FROM sys.dm_db_missing_index_details d
JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
JOIN sys.dm_db_missing_index_group_stats gs ON gs.group_handle = g.index_group_handle
WHERE d.database_id = DB_ID(@db)
GROUP BY d.database_id, d.object_id
ORDER BY SUM(gs.user_seeks) + SUM(gs.user_scans) DESC;
";
        var list = new List<MissingIndex>();
        using var cmd = new SqlCommand(sql, _conn);
        cmd.Parameters.AddWithValue("@db", (object)(dbName ?? ""));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MissingIndex(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                r.GetInt64(6), r.GetInt64(7), r.GetInt64(8)));
        return list;
    }

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
