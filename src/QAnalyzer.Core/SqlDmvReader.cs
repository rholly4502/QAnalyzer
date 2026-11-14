using Microsoft.Data.SqlClient;

namespace QAnalyzer.Core;

/// <summary>
/// Implementasi nyata via Microsoft.Data.SqlClient — baca DMV.
/// Hanya untuk koneksi lokal (WPF); bila tidak ada server hidup, jangan dipakai.
/// </summary>
public sealed class SqlDmvReader : ISqlMetadataReader
{
    private readonly SqlConnection _conn;

    public SqlDmvReader(string connectionString)
    {
        _conn = new SqlConnection(connectionString);
    }

    public bool IsConnected => _conn.State == System.Data.ConnectionState.Open;

    public void Open() => _conn.Open();

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

    public void Dispose() => _conn.Dispose();
}
