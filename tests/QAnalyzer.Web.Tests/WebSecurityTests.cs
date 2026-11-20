using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using QAnalyzer.Web.Security;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class WebSecurityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\r\n")]
    public void ValidateAnalyzeRequest_EmptyOrWhitespaceSql_Returns400(string? sql)
    {
        var req = sql is null ? null : new AnalyzeRequest { Sql = sql };

        var result = WebSecurityValidator.ValidateAnalyzeRequest(req, WebSecurityOptions.DefaultMaxSqlLength);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    [Fact]
    public void ValidateAnalyzeRequest_AtExactMaxLength_ReturnsNull()
    {
        var req = new AnalyzeRequest { Sql = new string('A', 128) };

        var result = WebSecurityValidator.ValidateAnalyzeRequest(req, 128);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateAnalyzeRequest_ExceedingMaxLength_Returns413()
    {
        var req = new AnalyzeRequest { Sql = new string('A', 129) };

        var result = WebSecurityValidator.ValidateAnalyzeRequest(req, 128);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
        Assert.Contains("128", result.Error);
    }

    [Fact]
    public void ResolveAllowedOrigins_ProductionWithNoConfig_ReturnsEmpty()
    {
        var config = new ConfigurationBuilder().Build();

        var origins = WebSecurityOptions.ResolveAllowedOrigins(config, isDevelopment: false);

        Assert.Empty(origins);
    }

    [Fact]
    public void ResolveAllowedOrigins_ProductionRejectsWildcard()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ALLOWED_ORIGINS"] = "*, https://rhol-ai.tech/",
            })
            .Build();

        var origins = WebSecurityOptions.ResolveAllowedOrigins(config, isDevelopment: false);

        var single = Assert.Single(origins);
        Assert.Equal("https://rhol-ai.tech", single);
    }

    [Fact]
    public void ResolveAllowedOrigins_DevelopmentWithNoConfig_ReturnsLocalhostOrigins()
    {
        var config = new ConfigurationBuilder().Build();

        var origins = WebSecurityOptions.ResolveAllowedOrigins(config, isDevelopment: true);

        Assert.NotEmpty(origins);
        Assert.DoesNotContain("*", origins);
        Assert.Contains("http://localhost:5173", origins);
    }

    [Fact]
    public void ResolveAllowedOrigins_CombinesSectionAndEnvWithoutDuplicates()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowedOrigins:0"] = "https://rhol-ai.tech",
                ["ALLOWED_ORIGINS"] = "https://RHOL-AI.TECH/, https://demo.rhol-ai.tech",
            })
            .Build();

        var origins = WebSecurityOptions.ResolveAllowedOrigins(config, isDevelopment: false);

        Assert.Equal(2, origins.Length);
        Assert.Contains("https://rhol-ai.tech", origins, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("https://demo.rhol-ai.tech", origins, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FromConfiguration_UsesSafeDefaultsWhenUnset()
    {
        var config = new ConfigurationBuilder().Build();

        var opts = WebSecurityOptions.FromConfiguration(config);

        Assert.Equal(WebSecurityOptions.DefaultMaxSqlLength, opts.MaxSqlLength);
        Assert.Equal(WebSecurityOptions.DefaultMaxRequestBodyBytes, opts.MaxRequestBodyBytes);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitPermitLimit, opts.RateLimitPermitLimit);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitWindowSeconds, opts.RateLimitWindowSeconds);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitQueueLimit, opts.RateLimitQueueLimit);
    }

    [Fact]
    public void FromConfiguration_ClampsInvalidValuesToSafeDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:MaxSqlLength"] = "-10",
                ["Security:MaxRequestBodyBytes"] = "0",
                ["Security:RateLimitPermitLimit"] = "abc",
                ["Security:RateLimitWindowSeconds"] = "-5",
                ["Security:RateLimitQueueLimit"] = "-1",
            })
            .Build();

        var opts = WebSecurityOptions.FromConfiguration(config);

        Assert.Equal(WebSecurityOptions.DefaultMaxSqlLength, opts.MaxSqlLength);
        Assert.Equal(WebSecurityOptions.DefaultMaxRequestBodyBytes, opts.MaxRequestBodyBytes);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitPermitLimit, opts.RateLimitPermitLimit);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitWindowSeconds, opts.RateLimitWindowSeconds);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitQueueLimit, opts.RateLimitQueueLimit);
    }
}
