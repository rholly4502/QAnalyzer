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

    /// <summary>Menunjukkan apakah koneksi terbuka.</summary>
    bool IsConnected { get; }
}

public sealed record QueryExecutionStat(string SqlText, long ExecutionCount, long TotalWorkerTimeMs, long TotalElapsedTimeMs, long TotalLogicalReads);

public sealed record IndexUsage(string Schema, string Table, string Index, long UserSeeks, long UserScans, long UserLookups, long UserUpdates);
