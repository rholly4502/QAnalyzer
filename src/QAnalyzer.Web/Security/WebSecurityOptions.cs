using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

namespace QAnalyzer.Web.Security;

/// <summary>
/// Daftar proxy/jaringan CIDR terpercaya untuk validasi header X-Forwarded-For / X-Forwarded-Proto.
/// </summary>
public sealed class TrustedProxyOptions
{
    public IReadOnlyList<IPAddress> Proxies { get; init; } = [];
    public IReadOnlyList<IPNetwork> Networks { get; init; } = [];
    public bool HasEntries => Proxies.Count > 0 || Networks.Count > 0;
}

/// <summary>
/// Konfigurasi keamanan untuk QAnalyzer.Web (batas payload, CORS aman, trusted proxy, dan rate limiter).
/// </summary>
public sealed class WebSecurityOptions
{
    public const int DefaultMaxSqlLength = 100_000;
    public const int DefaultMaxPlanLength = 100_000;
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
    public int MaxPlanLength { get; init; } = DefaultMaxPlanLength;
    public long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;
    public int RateLimitPermitLimit { get; init; } = DefaultRateLimitPermitLimit;
    public int RateLimitWindowSeconds { get; init; } = DefaultRateLimitWindowSeconds;
    public int RateLimitQueueLimit { get; init; } = DefaultRateLimitQueueLimit;

    public static WebSecurityOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection("Security");

        var maxSqlLength = ParsePositiveInt(section["MaxSqlLength"], DefaultMaxSqlLength);
        var maxPlanLength = ParsePositiveInt(section["MaxPlanLength"], DefaultMaxPlanLength);
        var maxRequestBodyBytes = ParsePositiveLong(section["MaxRequestBodyBytes"], DefaultMaxRequestBodyBytes);
        var permitLimit = ParsePositiveInt(section["RateLimitPermitLimit"], DefaultRateLimitPermitLimit);
        var windowSeconds = ParsePositiveInt(section["RateLimitWindowSeconds"], DefaultRateLimitWindowSeconds);
        var queueLimit = ParseNonNegativeInt(section["RateLimitQueueLimit"], DefaultRateLimitQueueLimit);

        return new WebSecurityOptions
        {
            MaxSqlLength = maxSqlLength,
            MaxPlanLength = maxPlanLength,
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

    /// <summary>
    /// Menyusun daftar proxy/CIDR terpercaya dari konfigurasi (<c>Security:TrustedProxies</c> / <c>TRUSTED_PROXIES</c>).
    /// Menolak wildcard, alamat unspecified (<c>0.0.0.0</c>/<c>::</c>), dan CIDR terlalu luas (&lt;/8 IPv4 atau &lt;/7 IPv6).
    /// </summary>
    public static TrustedProxyOptions ResolveTrustedProxies(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var candidates = new List<string>();
        foreach (var child in configuration.GetSection("Security:TrustedProxies").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                candidates.Add(child.Value);
            }
        }

        var envProxies = configuration["TRUSTED_PROXIES"];
        if (!string.IsNullOrWhiteSpace(envProxies))
        {
            candidates.AddRange(envProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var proxies = new List<IPAddress>();
        var seenProxies = new HashSet<IPAddress>();
        var networks = new List<IPNetwork>();
        var seenNetworks = new HashSet<IPNetwork>();

        foreach (var candidate in candidates)
        {
            if (!TryParseTrustedProxyEntry(candidate, out var ip, out var network))
            {
                continue;
            }

            if (ip is not null && seenProxies.Add(ip))
            {
                proxies.Add(ip);
            }
            else if (network is { } validNetwork && seenNetworks.Add(validNetwork))
            {
                networks.Add(validNetwork);
            }
        }

        return new TrustedProxyOptions
        {
            Proxies = proxies,
            Networks = networks,
        };
    }

    /// <summary>
    /// Menormalkan alamat IP klien untuk partition key rate limiter (termasuk IPv4-mapped IPv6).
    /// </summary>
    public static string ResolveRateLimitPartitionKey(IPAddress? remoteIp)
    {
        if (remoteIp is null)
        {
            return "unknown";
        }

        return (remoteIp.IsIPv4MappedToIPv6 ? remoteIp.MapToIPv4() : remoteIp).ToString();
    }

    private static bool TryParseTrustedProxyEntry(string? raw, out IPAddress? ip, out IPNetwork? network)
    {
        ip = null;
        network = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Contains('*', StringComparison.Ordinal) || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var slashIndex = trimmed.IndexOf('/');
        if (slashIndex >= 0)
        {
            if (trimmed.IndexOf('/', slashIndex + 1) >= 0)
            {
                return false;
            }

            var addrPart = trimmed[..slashIndex];
            if (!IPNetwork.TryParse(trimmed, out var parsedNetwork)
                || !IPAddress.TryParse(addrPart, out var rawBaseIp)
                || !rawBaseIp.Equals(parsedNetwork.BaseAddress))
            {
                return false;
            }

            if (parsedNetwork.BaseAddress.Equals(IPAddress.Any)
                || parsedNetwork.BaseAddress.Equals(IPAddress.IPv6Any))
            {
                return false;
            }

            if (parsedNetwork.BaseAddress.AddressFamily == AddressFamily.InterNetwork)
            {
                if (addrPart.Count(c => c == '.') != 3 || parsedNetwork.PrefixLength < 8)
                {
                    return false;
                }
            }
            else if (parsedNetwork.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (parsedNetwork.PrefixLength < 7)
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            network = parsedNetwork;
            return true;
        }

        if (!IPAddress.TryParse(trimmed, out var parsedIp))
        {
            return false;
        }

        if (parsedIp.AddressFamily == AddressFamily.InterNetwork)
        {
            if (parsedIp.Equals(IPAddress.Any)
                || trimmed.Count(c => c == '.') != 3
                || !string.Equals(parsedIp.ToString(), trimmed, StringComparison.Ordinal))
            {
                return false;
            }

            ip = parsedIp;
            return true;
        }

        if (parsedIp.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (parsedIp.IsIPv4MappedToIPv6)
            {
                var mapped = parsedIp.MapToIPv4();
                if (mapped.Equals(IPAddress.Any))
                {
                    return false;
                }

                ip = mapped;
                return true;
            }

            if (parsedIp.Equals(IPAddress.IPv6Any) || !trimmed.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }

            ip = parsedIp;
            return true;
        }

        return false;
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
