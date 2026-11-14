using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace QAnalyzer.Core;

/// <summary>Data yang dikirim ke advisor untuk rekomendasi tuning.</summary>
public sealed record TuningRequest(string Sql, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<MissingIndex> MissingIndexes, CachedQueryStat? CachedStat);

/// <summary>Balikan advisor (bebas format, umumnya Markdown).</summary>
public sealed record TuningResponse(bool Success, string Content, string? Error);

/// <summary>
/// Mengirim query + hasil DMV ke gateway Hermes (OpenAI-compatible) untuk
/// mendapat rekomendasi tuning. Murni HTTP; endpoint & key dari settings.
/// </summary>
public sealed class HermesTuningAdvisor
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly TuningMemory? _memory;

    public HermesTuningAdvisor(HttpClient http, string endpoint, string apiKey, TuningMemory? memory = null)
    {
        _http = http;
        _endpoint = endpoint.TrimEnd('/') + "/chat/completions";
        _apiKey = apiKey;
        _memory = memory;
    }

    public async Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(request);
        if (_memory is not null) prompt += _memory.BuildContext();
        var body = new
        {
            model = "hermes-agent",
            messages = new object[]
            {
                new { role = "system", content = "Kamu adalah asisten tuning SQL Server. Berikan rekomendasi konkret, ringkas, dalam Bahasa Indonesia, berdasarkan query + data DMV. Sebutkan hal yang relevan (index, maxdop, statistik, rewrite) dan kekurangan data yang tidak ada." },
                new { role = "user", content = prompt },
            },
            temperature = 0.3d,
        };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
            req.Content = JsonContent.Create(body);
            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new(false, string.Empty, $"HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");
            using var doc = JsonDocument.Parse(text);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (_memory is not null && content is not null)
            {
                var missing = string.Join(", ", request.MissingIndexes.Select(m => $"{m.Schema}.{m.Table}({m.EqualityColumns})"));
                var summary = request.Sql.Replace('\n', ' ').Trim();
                _memory.AppendSession(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), summary, missing, content);
            }
            return new(true, content ?? string.Empty, null);
        }
        catch (Exception ex)
        {
            return new(false, string.Empty, ex.Message);
        }
    }

    private static string BuildPrompt(TuningRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Query yang dianalisis");
        sb.AppendLine("```sql");
        sb.AppendLine(r.Sql);
        sb.AppendLine("```");

        sb.AppendLine("\n# Hasil analisis statis (rule)");
        if (r.Diagnostics.Count == 0) sb.AppendLine("- (tidak ada temuan)");
        else foreach (var d in r.Diagnostics)
            sb.AppendLine($"- [{d.Level}] {d.RuleId}: {d.Message}");

        if (r.MissingIndexes.Count > 0)
        {
            sb.AppendLine("\n# Missing index (dari DMV)");
            foreach (var m in r.MissingIndexes)
                sb.AppendLine($"- {m.Schema}.{m.Table}: equality=[{m.EqualityColumns}] inequality=[{m.InequalityColumns}] include=[{m.IncludeColumns}] | seeks={m.UserSeeks} scans={m.UserScans} impact~{m.AvgUserImpact}%");
        }

        if (r.CachedStat is not null)
        {
            sb.AppendLine("\n# Statistik historis (plan cache)");
            sb.AppendLine($"- executions={r.CachedStat.ExecutionCount}, elapsed={r.CachedStat.TotalElapsedMs}ms, worker={r.CachedStat.TotalWorkerMs}ms, reads={r.CachedStat.TotalLogicalReads}, plan age={r.CachedStat.PlanAgeMinutes}mnt");
        }

        sb.AppendLine("\nBeri saran tuning SQL Server yang konkret.");
        return sb.ToString();
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
