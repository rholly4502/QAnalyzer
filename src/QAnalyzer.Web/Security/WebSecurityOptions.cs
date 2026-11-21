using Microsoft.Extensions.Configuration;

namespace QAnalyzer.Web.Security;

/// <summary>
/// Konfigurasi keamanan untuk QAnalyzer.Web (batas payload, CORS aman, dan rate limiter).
/// </summary>
public sealed class WebSecurityOptions
{
    public const int DefaultMaxSqlLength = 100_000;
    public const long DefaultMaxRequestBodyBytes = 262_144; // 256 KB
    public const int DefaultRateLimitPermitLimit = 30;
    public const int DefaultRateLimitWindowSeconds = 60;
    public const int DefaultRateLimitQueueLimit = 0;

    public static readonly string[] DefaultDevOrigins =
    [
        "http://localhost:5173",
        "https://localhost:5173",
        "http://localhost:5213",
        "https://localhost:5213",
    ];

    public int MaxSqlLength { get; init; } = DefaultMaxSqlLength;
    public long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;
    public int RateLimitPermitLimit { get; init; } = DefaultRateLimitPermitLimit;
    public int RateLimitWindowSeconds { get; init; } = DefaultRateLimitWindowSeconds;
    public int RateLimitQueueLimit { get; init; } = DefaultRateLimitQueueLimit;

    public static WebSecurityOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Security");

        var maxSqlLength = ParsePositiveInt(section["MaxSqlLength"], DefaultMaxSqlLength);
        var maxRequestBodyBytes = ParsePositiveLong(section["MaxRequestBodyBytes"], DefaultMaxRequestBodyBytes);
        var permitLimit = ParsePositiveInt(section["RateLimitPermitLimit"], DefaultRateLimitPermitLimit);
        var windowSeconds = ParsePositiveInt(section["RateLimitWindowSeconds"], DefaultRateLimitWindowSeconds);
        var queueLimit = ParseNonNegativeInt(section["RateLimitQueueLimit"], DefaultRateLimitQueueLimit);

        return new WebSecurityOptions
        {
            MaxSqlLength = maxSqlLength,
            MaxRequestBodyBytes = maxRequestBodyBytes,
            RateLimitPermitLimit = permitLimit,
            RateLimitWindowSeconds = windowSeconds,
            RateLimitQueueLimit = queueLimit,
        };
    }

    /// <summary>
    /// Menyusun daftar origin CORS yang diizinkan. Wildcard "*" selalu ditolak.
    /// Di Production tanpa konfigurasi eksplisit, mengembalikan array kosong (hanya same-origin).
    /// </summary>
    public static string[] ResolveAllowedOrigins(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var candidates = new List<string>();

        foreach (var child in configuration.GetSection("Security:AllowedOrigins").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                candidates.Add(child.Value);
            }
        }

        var envOrigins = configuration["ALLOWED_ORIGINS"];
        if (!string.IsNullOrWhiteSpace(envOrigins))
        {
            candidates.AddRange(envOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var sanitized = new List<string>();
        foreach (var candidate in candidates)
        {
            if (TryNormalizeAllowedOrigin(candidate, out var normalized))
            {
                sanitized.Add(normalized);
            }
        }

        var distinct = sanitized
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (distinct.Length > 0)
        {
            return distinct;
        }

        return isDevelopment ? [.. DefaultDevOrigins] : [];
    }

    private static bool TryNormalizeAllowedOrigin(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed)
            || trimmed.Contains('*', StringComparison.Ordinal)
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
            || !string.IsNullOrEmpty(uri.Fragment)
            || (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    private static int ParsePositiveInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    private static long ParsePositiveLong(string? raw, long fallback) =>
        long.TryParse(raw, out var value) && value > 0 ? value : fallback;

    private static int ParseNonNegativeInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value >= 0 ? value : fallback;
}
