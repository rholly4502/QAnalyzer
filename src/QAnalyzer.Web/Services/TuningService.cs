using System.Net.Http;
using Microsoft.Extensions.Configuration;
using QAnalyzer.Core;
using QAnalyzer.Core.Rules;

namespace QAnalyzer.Web.Services;

public sealed class TuneRequest
{
    public string Sql { get; set; } = string.Empty;
    public string? Profile { get; set; }
    public string? SqlPlanXml { get; set; }
}

public sealed class TuneResponse
{
    public bool Success { get; init; }
    public string Content { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int TotalDiagnostics { get; init; }
    public bool HasCritical { get; init; }
    public List<DiagnosticDto> Diagnostics { get; init; } = [];
}

/// <summary>
/// Konfigurasi gateway AI tuning yang dibaca dari environment variable / konfigurasi server.
/// API key tidak pernah di-hardcode atau diekspos ke client.
/// </summary>
public sealed class TuningGatewayOptions
{
    public string Endpoint { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;

    public bool IsConfigured =>
        TryNormalizeEndpoint(Endpoint, out _) && !string.IsNullOrWhiteSpace(ApiKey);

    public static bool TryNormalizeEndpoint(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Any(char.IsWhiteSpace)
            || trimmed.Contains('?', StringComparison.Ordinal)
            || trimmed.Contains('#', StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return true;
    }

    public static TuningGatewayOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var endpoint = configuration["HERMES_TUNING_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = configuration["Tuning:Endpoint"];
        }

        var apiKey = configuration["HERMES_TUNING_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = configuration["Tuning:ApiKey"];
        }

        return new TuningGatewayOptions
        {
            Endpoint = TryNormalizeEndpoint(endpoint, out var normalizedEndpoint) ? normalizedEndpoint : string.Empty,
            ApiKey = apiKey?.Trim() ?? string.Empty,
        };
    }
}

public interface ITuningAdvisorClient
{
    Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default);
}

public sealed class HermesTuningAdvisorClient(HttpClient httpClient, TuningGatewayOptions options) : ITuningAdvisorClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly TuningGatewayOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
        {
            return Task.FromResult(new TuningResponse(
                false,
                string.Empty,
                "Layanan AI tuning belum dikonfigurasi di server."));
        }

        // Jalur Web tidak memakai TuningMemory bersama agar tidak saling menimpa antar pengguna.
        var advisor = new HermesTuningAdvisor(_httpClient, _options.Endpoint, _options.ApiKey, memory: null);
        return advisor.AskAsync(request, ct);
    }
}

/// <summary>
/// Wrapper AI tuning aman untuk jalur Web: menggabungkan analisis statis T-SQL dan
/// opsional XML execution plan (.sqlplan) tanpa koneksi database atau DMV.
/// </summary>
public sealed class TuningService(ITuningAdvisorClient advisorClient)
{
    private static readonly ISqlRule[] DefaultRules =
    [
        new SqlSyntaxErrorRule(),
        new SelectStarRule(),
        new NoLockRule(),
        new UpdateWithoutWhereRule(),
        new DeleteWithoutWhereRule(),
        new TruncateTableRule(),
        new NonSargablePredicateRule(),
    ];

    private readonly ITuningAdvisorClient _advisorClient = advisorClient ?? throw new ArgumentNullException(nameof(advisorClient));
    private readonly ExecutionPlanParser _planParser = new();

    public async Task<TuneResponse> TuneAsync(TuneRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var settings = new AnalyzerSettings
        {
            Profile = string.Equals(request.Profile, "reporting", StringComparison.OrdinalIgnoreCase)
                ? AnalysisProfile.Reporting
                : AnalysisProfile.General,
        };

        var analyzer = new SqlAnalyzer(DefaultRules, settings);
        var report = analyzer.Analyze(request.Sql);

        var combinedDiagnostics = new List<Diagnostic>(report.Diagnostics);
        if (!string.IsNullOrWhiteSpace(request.SqlPlanXml))
        {
            foreach (var d in _planParser.Parse(request.SqlPlanXml))
            {
                var resolved = RuleLevel.Resolve(d.Level, settings, d.RuleId);
                combinedDiagnostics.Add(resolved == d.Level ? d : d with { Level = resolved });
            }
        }

        var tuningRequest = new TuningRequest(
            request.Sql,
            combinedDiagnostics,
            MissingIndexes: [],
            CachedStat: null);

        var advisorResp = await _advisorClient.AskAsync(tuningRequest, ct);

        return new TuneResponse
        {
            Success = advisorResp.Success,
            Content = advisorResp.Content,
            Error = advisorResp.Error,
            TotalDiagnostics = combinedDiagnostics.Count,
            HasCritical = combinedDiagnostics.Any(d => d.Level == RiskLevel.Critical),
            Diagnostics = combinedDiagnostics
                .Select(d => new DiagnosticDto(
                    d.Level.ToString(), d.RuleId, d.Title, d.Message,
                    d.Suggestion, d.Line, d.Column))
                .ToList(),
        };
    }
}
