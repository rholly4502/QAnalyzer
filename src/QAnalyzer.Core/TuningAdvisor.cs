using System.Net.Http;
using System.Net.Http.Headers;
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
/// mendapat rekomendasi tuning. Murni HTTP; endpoint &amp; key dari settings.
/// </summary>
public sealed class HermesTuningAdvisor
{
    public const int MaxResponseBytes = 512 * 1024;

    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly TuningMemory? _memory;

    public HermesTuningAdvisor(HttpClient http, string endpoint, string apiKey, TuningMemory? memory = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _http = http;
        _endpoint = NormalizeEndpoint(endpoint);
        _apiKey = ValidateApiKey(apiKey);
        _memory = memory;
    }

    public async Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

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
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = JsonContent.Create(body);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var (tooLarge, text) = await ReadBoundedResponseAsync(resp.Content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                return tooLarge
                    ? new(false, string.Empty, $"HTTP {(int)resp.StatusCode}: Respons error melebihi batas maksimal {MaxResponseBytes} byte.")
                    : new(false, string.Empty, $"HTTP {(int)resp.StatusCode}: {Truncate(text, 300)}");
            }

            if (tooLarge)
            {
                return new(false, string.Empty, $"Respons gateway AI melebihi batas maksimal {MaxResponseBytes} byte.");
            }

            using var doc = JsonDocument.Parse(text);
            if (!TryExtractContent(doc.RootElement, out var content))
            {
                return new(false, string.Empty, "Respons gateway AI tidak valid atau tidak memuat konten saran.");
            }

            if (_memory is not null)
            {
                var missing = string.Join(", ", request.MissingIndexes.Select(m => $"{m.Schema}.{m.Table}({m.EqualityColumns})"));
                var summary = request.Sql.Replace('\r', ' ').Replace('\n', ' ').Trim();
                _memory.AppendSession(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), summary, missing, content);
            }

            return new(true, content, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new(false, string.Empty, "Permintaan ke gateway AI melebihi batas waktu.");
        }
        catch (JsonException)
        {
            return new(false, string.Empty, "Respons gateway AI bukan JSON yang valid.");
        }
        catch (Exception ex)
        {
            return new(false, string.Empty, ex.Message);
        }
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = endpoint.Trim();
        if (trimmed.Any(char.IsWhiteSpace)
            || trimmed.Contains('?', StringComparison.Ordinal)
            || trimmed.Contains('#', StringComparison.Ordinal)
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException(
                "Endpoint gateway AI tidak valid; gunakan URL HTTP/HTTPS absolut tanpa query atau fragment.",
                nameof(endpoint));
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/chat/completions";
    }

    private static string ValidateApiKey(string apiKey)
    {
        var trimmed = apiKey.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            throw new ArgumentException(
                "API key gateway AI tidak valid atau mengandung karakter kontrol/whitespace.",
                nameof(apiKey));
        }

        return trimmed;
    }

    private static async Task<(bool TooLarge, string Text)> ReadBoundedResponseAsync(HttpContent? content, CancellationToken ct)
    {
        if (content is null)
        {
            return (false, string.Empty);
        }

        if (content.Headers.ContentLength is long declaredLength && declaredLength > MaxResponseBytes)
        {
            return (true, string.Empty);
        }

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }

            if (ms.Length + read > MaxResponseBytes)
            {
                return (true, string.Empty);
            }

            ms.Write(buffer, 0, read);
        }

        var encoding = ResolveEncoding(content.Headers.ContentType?.CharSet);
        var text = encoding.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return (false, text);
    }

    private static Encoding ResolveEncoding(string? charSet)
    {
        if (string.IsNullOrWhiteSpace(charSet))
        {
            return Encoding.UTF8;
        }

        var cleaned = charSet.Trim().Trim('"');
        if (cleaned.Length == 0)
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(cleaned);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static bool TryExtractContent(JsonElement root, out string content)
    {
        content = string.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            return false;
        }

        var firstChoice = choices[0];
        if (firstChoice.ValueKind != JsonValueKind.Object
            || !firstChoice.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var contentElement)
            || contentElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var extracted = contentElement.GetString();
        if (string.IsNullOrWhiteSpace(extracted))
        {
            return false;
        }

        content = extracted;
        return true;
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
