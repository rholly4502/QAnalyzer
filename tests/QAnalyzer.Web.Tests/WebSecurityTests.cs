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
    public void ValidateAnalyzeRequest_PlanOnlyWithoutSql_ReturnsNull()
    {
        var req = new AnalyzeRequest
        {
            Sql = "",
            SqlPlanXml = "<ShowPlanXML />",
        };

        var result = WebSecurityValidator.ValidateAnalyzeRequest(req, 128, 256);

        Assert.Null(result);
    }

    [Fact]
    public void ValidateAnalyzeRequest_PlanExceedingMaxLength_Returns413()
    {
        var req = new AnalyzeRequest
        {
            Sql = "SELECT 1;",
            SqlPlanXml = new string('P', 201),
        };

        var result = WebSecurityValidator.ValidateAnalyzeRequest(req, 1000, 200);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
        Assert.Contains("200", result.Error);
    }

    [Fact]
    public void ValidateAnalyzeRequest_WhitespaceSqlExceedingMaxLengthWithPlan_Returns413()
    {
        var req = new AnalyzeRequest
        {
            Sql = new string(' ', 129),
            SqlPlanXml = "<ShowPlanXML />",
        };

        var result = WebSecurityValidator.ValidateAnalyzeRequest(req, 128, 256);

        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, result.StatusCode);
        Assert.Contains("128", result.Error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateAnalyzeRequest_InvalidMaxPlanLength_ThrowsArgumentOutOfRangeException(int invalidMaxPlanLength)
    {
        var req = new AnalyzeRequest { Sql = "SELECT 1;" };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => WebSecurityValidator.ValidateAnalyzeRequest(req, 128, invalidMaxPlanLength));
        Assert.Equal("maxPlanLength", ex.ParamName);
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
        Assert.Equal(WebSecurityOptions.DefaultMaxPlanLength, opts.MaxPlanLength);
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
                ["Security:MaxPlanLength"] = "0",
                ["Security:MaxRequestBodyBytes"] = "0",
                ["Security:RateLimitPermitLimit"] = "abc",
                ["Security:RateLimitWindowSeconds"] = "-5",
                ["Security:RateLimitQueueLimit"] = "-1",
            })
            .Build();

        var opts = WebSecurityOptions.FromConfiguration(config);

        Assert.Equal(WebSecurityOptions.DefaultMaxSqlLength, opts.MaxSqlLength);
        Assert.Equal(WebSecurityOptions.DefaultMaxPlanLength, opts.MaxPlanLength);
        Assert.Equal(WebSecurityOptions.DefaultMaxRequestBodyBytes, opts.MaxRequestBodyBytes);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitPermitLimit, opts.RateLimitPermitLimit);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitWindowSeconds, opts.RateLimitWindowSeconds);
        Assert.Equal(WebSecurityOptions.DefaultRateLimitQueueLimit, opts.RateLimitQueueLimit);
    }

    [Fact]
    public void FromConfiguration_ReadsValidMaxPlanLength()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:MaxPlanLength"] = "250000",
            })
            .Build();

        var opts = WebSecurityOptions.FromConfiguration(config);

        Assert.Equal(250_000, opts.MaxPlanLength);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("https://*.rhol-ai.tech")]
    [InlineData("https://rhol-ai.tech/subpath")]
    [InlineData("https://user:pass@rhol-ai.tech")]
    [InlineData("not-a-uri")]
    public void ResolveAllowedOrigins_RejectsMalformedWildcardAndNonHttpOrigins(string invalidOrigin)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ALLOWED_ORIGINS"] = invalidOrigin,
            })
            .Build();

        Assert.Empty(WebSecurityOptions.ResolveAllowedOrigins(config, isDevelopment: false));

        var devOrigins = WebSecurityOptions.ResolveAllowedOrigins(config, isDevelopment: true);
        Assert.Equal(WebSecurityOptions.DefaultDevOrigins, devOrigins);
    }

    [Fact]
    public void ResolveAllowedOrigins_NormalizesValidOriginsAndDropsInvalidOnes()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AllowedOrigins:0"] = "https://RHOL-AI.TECH/",
                ["Security:AllowedOrigins:1"] = "https://rhol-ai.tech/subpath",
                ["ALLOWED_ORIGINS"] = "null, http://localhost:5173/, https://*.evil.test, javascript:alert(1)",
            })
            .Build();

        var origins = WebSecurityOptions.ResolveAllowedOrigins(config, isDevelopment: false);

        Assert.Equal(["https://rhol-ai.tech", "http://localhost:5173"], origins);
    }
}
