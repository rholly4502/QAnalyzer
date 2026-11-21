namespace QAnalyzer.Core;

/// <summary>
/// Abstraksi pembaca metadata/statistik SQL Server via DMV. Dipisah dari UI
/// dan dari koneksi nyata agar bisa diuji (fake) dan dipakai ulang oleh WPF
/// (koneksi lokal) maupun tools lain.
/// </summary>
public interface ISqlMetadataReader : IDisposable
{
    /// <summary>Statistik eksekusi query teratas (sys.dm_exec_query_stats).</summary>
    IReadOnlyList<QueryExecutionStat> GetTopQueryStats(int top);

    /// <summary>Penggunaan index yang terabaikan (sys.dm_db_index_usage_stats).</summary>
    IReadOnlyList<IndexUsage> GetIndexUsage(string? schema = null, string? table = null);

    /// <summary>
    /// Statistik historis query yang cocok dengan query text (via plan cache/DMV),
    /// tanpa mengeksekusi. Mengembalikan null jika tidak ada plan tersimpan.
    /// </summary>
    CachedQueryStat? GetStatsForSql(string sqlText);

    /// <summary>
    /// Index yang "missing" menurut optimizer (sys.dm_db_missing_index_details):
    /// sinyal bahwa ada query yang TIDAK bisa memakai index karena belum ada.
    /// </summary>
    IReadOnlyList<MissingIndex> GetMissingIndexes(string? dbName = null);

    /// <summary>
    /// Mengambil XML Execution Plan dari SQL Server tanpa mengeksekusi mutasi data:
    /// pertama mencari di plan cache (DMV), lalu fallback ke estimated plan (SHOWPLAN_XML)
    /// hanya untuk query baca yang lolos validasi AST.
    /// </summary>
    Task<ExecutionPlanResult?> GetExecutionPlanXmlAsync(string sqlText, CancellationToken ct = default);

    /// <summary>Menunjukkan apakah koneksi terbuka.</summary>
    bool IsConnected { get; }
}

public enum ExecutionPlanSource { PlanCache, EstimatedShowPlan }

public sealed record ExecutionPlanResult(string PlanXml, ExecutionPlanSource Source);

public sealed record MissingIndex(string Database, string Schema, string Table, string? EqualityColumns, string? InequalityColumns, string? IncludeColumns, long UserSeeks, long UserScans, long AvgUserImpact);

public sealed record CachedQueryStat(string SqlText, long ExecutionCount, long TotalElapsedMs, long TotalWorkerMs, long TotalLogicalReads, int PlanAgeMinutes);


public sealed record QueryExecutionStat(string SqlText, long ExecutionCount, long TotalWorkerTimeMs, long TotalElapsedTimeMs, long TotalLogicalReads);

public sealed record IndexUsage(string Schema, string Table, string Index, long UserSeeks, long UserScans, long UserLookups, long UserUpdates);
