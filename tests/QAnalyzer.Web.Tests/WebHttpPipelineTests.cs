using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class WebHttpPipelineTests
{
    private sealed class StubAdvisorClient(TuningResponse response) : ITuningAdvisorClient
    {
        public Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default) =>
            Task.FromResult(response);
    }

    private sealed class TestHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<TestHost> StartTestHostAsync(
        IDictionary<string, string?>? config = null,
        string? contentRootPath = null,
        ITuningAdvisorClient? advisorClient = null)
    {
        var mergedConfig = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HERMES_TUNING_ENDPOINT"] = string.Empty,
            ["HERMES_TUNING_API_KEY"] = string.Empty,
            ["Tuning:Endpoint"] = string.Empty,
            ["Tuning:ApiKey"] = string.Empty,
        };

        if (config is not null)
        {
            foreach (var kv in config)
            {
                mergedConfig[kv.Key] = kv.Value;
            }
        }

        var builder = Program.CreateAppBuilder(
            environmentName: Environments.Production,
            contentRootPath: contentRootPath,
            configurationOverrides: mergedConfig);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        if (advisorClient is not null)
        {
            builder.Services.AddSingleton(advisorClient);
        }

        var app = Program.BuildApp(builder);
        await app.StartAsync();

        var baseAddress = new Uri(app.Urls.First());
        var client = new HttpClient { BaseAddress = baseAddress };
        return new TestHost(app, client);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    [Theory]
    [InlineData("/api/health")]
    [InlineData("/analyzer/api/health")]
    public async Task HealthEndpoint_WorksOnRootAndSubpath(string path)
    {
        await using var host = await StartTestHostAsync();

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_WorksOnRootAndSubpath(string path)
    {
        await using var host = await StartTestHostAsync();
        using var body = new StringContent("""{"sql":"SELECT * FROM dbo.Orders;"}""", Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync(path, body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        var diagnostics = json.RootElement.GetProperty("diagnostics");
        Assert.Contains(diagnostics.EnumerateArray(), d => d.GetProperty("ruleId").GetString() == "SELECT_STAR");
    }

    [Fact]
    public async Task AnalyzeEndpoint_WithSqlPlanXml_WorksOnRootAndSubpath_AndRejectsInvalidOrOversizedPlan()
    {
        const string planXml = "<ShowPlanXML><RelOp NodeId=\"1\" PhysicalOp=\"Table Scan\" LogicalOp=\"Table Scan\"><TableScan><Object Schema=\"[dbo]\" Table=\"[Orders]\" /></TableScan></RelOp></ShowPlanXML>";
        var combinedPayload = JsonSerializer.Serialize(new
        {
            sql = "SELECT * FROM dbo.Orders;",
            sqlPlanXml = planXml,
        });

        await using var host = await StartTestHostAsync();

        foreach (var path in new[] { "/api/analyze", "/analyzer/api/analyze" })
        {
            using var body = new StringContent(combinedPayload, Encoding.UTF8, "application/json");
            var response = await host.Client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = await ReadJsonAsync(response);
            Assert.True(json.RootElement.GetProperty("hasExecutionPlan").GetBoolean());
            Assert.Equal(1, json.RootElement.GetProperty("planDiagnosticCount").GetInt32());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(diagnostics, d => d.GetProperty("ruleId").GetString() == "SELECT_STAR");
            Assert.Contains(diagnostics, d => d.GetProperty("ruleId").GetString() == ExecutionPlanParser.RuleTableScan);
        }

        var planOnlyPayload = JsonSerializer.Serialize(new
        {
            sql = "",
            sqlPlanXml = planXml,
        });
        using (var planOnlyBody = new StringContent(planOnlyPayload, Encoding.UTF8, "application/json"))
        {
            var planOnlyResp = await host.Client.PostAsync("/analyzer/api/analyze", planOnlyBody);
            Assert.Equal(HttpStatusCode.OK, planOnlyResp.StatusCode);
            using var planOnlyJson = await ReadJsonAsync(planOnlyResp);
            Assert.True(planOnlyJson.RootElement.GetProperty("hasExecutionPlan").GetBoolean());
            var diag = Assert.Single(planOnlyJson.RootElement.GetProperty("diagnostics").EnumerateArray());
            Assert.Equal(ExecutionPlanParser.RuleTableScan, diag.GetProperty("ruleId").GetString());
        }

        foreach (var invalidXml in new[]
        {
            "<ShowPlanXML><broken>",
            "<?xml version=\"1.0\"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]><ShowPlanXML>&xxe;</ShowPlanXML>",
        })
        {
            var invalidPayload = JsonSerializer.Serialize(new
            {
                sql = "SELECT Id FROM dbo.Orders;",
                sqlPlanXml = invalidXml,
            });
            using var badBody = new StringContent(invalidPayload, Encoding.UTF8, "application/json");
            var badResp = await host.Client.PostAsync("/analyzer/api/analyze", badBody);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
            Assert.Equal("application/json", badResp.Content.Headers.ContentType?.MediaType);
            using var err = await ReadJsonAsync(badResp);
            Assert.False(string.IsNullOrWhiteSpace(err.RootElement.GetProperty("error").GetString()));
        }

        var limitConfig = new Dictionary<string, string?>
        {
            ["Security:MaxPlanLength"] = "64",
        };
        await using var limitedHost = await StartTestHostAsync(limitConfig);
        var oversizedPayload = JsonSerializer.Serialize(new
        {
            sql = "SELECT 1;",
            sqlPlanXml = "<ShowPlanXML>" + new string('A', 80) + "</ShowPlanXML>",
        });
        using var oversizedBody = new StringContent(oversizedPayload, Encoding.UTF8, "application/json");
        var oversizedResp = await limitedHost.Client.PostAsync("/analyzer/api/analyze", oversizedBody);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResp.StatusCode);
        Assert.Equal("application/json", oversizedResp.Content.Headers.ContentType?.MediaType);
        using var oversizedErr = await ReadJsonAsync(oversizedResp);
        Assert.False(string.IsNullOrWhiteSpace(oversizedErr.RootElement.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData("/api/tune")]
    [InlineData("/analyzer/api/tune")]
    public async Task TuneEndpoint_WhenUnconfigured_Returns503JsonOnRootAndSubpath(string path)
    {
        await using var host = await StartTestHostAsync();
        using var body = new StringContent("""{"sql":"SELECT Id FROM dbo.Orders;"}""", Encoding.UTF8, "application/json");

        var response = await host.Client.PostAsync(path, body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task TuneEndpoint_OnSubpath_ConfiguredReturns200AndInvalidPlanReturns400Json()
    {
        var config = new Dictionary<string, string?>
        {
            ["HERMES_TUNING_ENDPOINT"] = "https://gateway.example.local/v1",
            ["HERMES_TUNING_API_KEY"] = "test-key",
        };
        var stub = new StubAdvisorClient(new TuningResponse(true, "Tambahkan index.", null));

        await using var host = await StartTestHostAsync(config, advisorClient: stub);

        using var okBody = new StringContent("""{"sql":"SELECT * FROM dbo.Orders;"}""", Encoding.UTF8, "application/json");
        var okResp = await host.Client.PostAsync("/analyzer/api/tune", okBody);
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        using (var okJson = await ReadJsonAsync(okResp))
        {
            Assert.True(okJson.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal("Tambahkan index.", okJson.RootElement.GetProperty("content").GetString());
        }

        using var badPlanBody = new StringContent(
            """{"sql":"SELECT Id FROM dbo.Orders;","sqlPlanXml":"<ShowPlanXML><broken>"}""",
            Encoding.UTF8,
            "application/json");
        var badResp = await host.Client.PostAsync("/analyzer/api/tune", badPlanBody);
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
        using var badJson = await ReadJsonAsync(badResp);
        Assert.False(string.IsNullOrWhiteSpace(badJson.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Responses_EmitSecurityHeaders_AndSuppressServerHeader()
    {
        await using var host = await StartTestHostAsync();

        var response = await host.Client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Server"));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.True(response.Headers.TryGetValues("Permissions-Policy", out var permissionsValues));
        Assert.Contains("camera=()", string.Join(",", permissionsValues));
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var cspValues));
        var csp = string.Join(";", cspValues);
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
    }

    [Fact]
    public async Task ApiErrors_MalformedJsonUnsupportedMediaTypeAndBodyLimit_ReturnConsistentJson()
    {
        var config = new Dictionary<string, string?>
        {
            ["Security:MaxRequestBodyBytes"] = "256",
        };
        await using var host = await StartTestHostAsync(config);

        using var malformed = new StringContent("{invalid", Encoding.UTF8, "application/json");
        var badJsonResp = await host.Client.PostAsync("/api/analyze", malformed);
        Assert.Equal(HttpStatusCode.BadRequest, badJsonResp.StatusCode);
        Assert.Equal("application/json", badJsonResp.Content.Headers.ContentType?.MediaType);
        using (var err = await ReadJsonAsync(badJsonResp))
        {
            Assert.False(string.IsNullOrWhiteSpace(err.RootElement.GetProperty("error").GetString()));
        }

        using var wrongType = new StringContent("SELECT 1;", Encoding.UTF8, "text/plain");
        var mediaResp = await host.Client.PostAsync("/api/analyze", wrongType);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, mediaResp.StatusCode);
        Assert.Equal("application/json", mediaResp.Content.Headers.ContentType?.MediaType);
        using (var err = await ReadJsonAsync(mediaResp))
        {
            Assert.False(string.IsNullOrWhiteSpace(err.RootElement.GetProperty("error").GetString()));
        }

        var oversizedPayload = "{\"sql\":\"SELECT '" + new string('A', 350) + "';\"}";
        using var tooLarge = new StringContent(oversizedPayload, Encoding.UTF8, "application/json");
        var largeResp = await host.Client.PostAsync("/api/analyze", tooLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, largeResp.StatusCode);
        Assert.Equal("application/json", largeResp.Content.Headers.ContentType?.MediaType);
        using (var err = await ReadJsonAsync(largeResp))
        {
            Assert.False(string.IsNullOrWhiteSpace(err.RootElement.GetProperty("error").GetString()));
        }
    }

    [Fact]
    public async Task RateLimiter_Returns429JsonWithRetryAfterHeader()
    {
        var config = new Dictionary<string, string?>
        {
            ["Security:RateLimitPermitLimit"] = "1",
            ["Security:RateLimitWindowSeconds"] = "60",
            ["Security:RateLimitQueueLimit"] = "0",
        };
        await using var host = await StartTestHostAsync(config);

        using var firstBody = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json");
        var first = await host.Client.PostAsync("/analyzer/api/analyze", firstBody);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var secondBody = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json");
        var second = await host.Client.PostAsync("/analyzer/api/analyze", secondBody);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("application/json", second.Content.Headers.ContentType?.MediaType);
        Assert.True(second.Headers.TryGetValues("Retry-After", out var retryValues));
        var retrySeconds = int.Parse(Assert.Single(retryValues));
        Assert.True(retrySeconds > 0);

        using var err = await ReadJsonAsync(second);
        Assert.False(string.IsNullOrWhiteSpace(err.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task RateLimiter_WithoutTrustedProxies_IgnoresSpoofedXForwardedFor()
    {
        var config = new Dictionary<string, string?>
        {
            ["Security:RateLimitPermitLimit"] = "1",
            ["Security:RateLimitWindowSeconds"] = "60",
            ["Security:RateLimitQueueLimit"] = "0",
        };
        await using var host = await StartTestHostAsync(config);

        using var firstReq = new HttpRequestMessage(HttpMethod.Post, "/analyzer/api/analyze")
        {
            Content = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json"),
        };
        firstReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        var firstResp = await host.Client.SendAsync(firstReq);
        Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);

        using var secondReq = new HttpRequestMessage(HttpMethod.Post, "/analyzer/api/analyze")
        {
            Content = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json"),
        };
        secondReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
        var secondResp = await host.Client.SendAsync(secondReq);
        Assert.Equal(HttpStatusCode.TooManyRequests, secondResp.StatusCode);
    }

    [Fact]
    public async Task RateLimiter_WithTrustedProxy_PartitionsByClientIpAndBlocksLeftmostSpoofing()
    {
        var config = new Dictionary<string, string?>
        {
            ["Security:RateLimitPermitLimit"] = "1",
            ["Security:RateLimitWindowSeconds"] = "60",
            ["Security:RateLimitQueueLimit"] = "0",
            ["TRUSTED_PROXIES"] = "127.0.0.0/8",
        };
        await using var host = await StartTestHostAsync(config);

        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/analyzer/api/analyze")
        {
            Content = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json"),
        };
        req1.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        var resp1 = await host.Client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/analyzer/api/analyze")
        {
            Content = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json"),
        };
        req2.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.10");
        var resp2 = await host.Client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp2.StatusCode);

        using var spoofReq = new HttpRequestMessage(HttpMethod.Post, "/analyzer/api/analyze")
        {
            Content = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json"),
        };
        spoofReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.99, 203.0.113.10");
        var spoofResp = await host.Client.SendAsync(spoofReq);
        Assert.Equal(HttpStatusCode.TooManyRequests, spoofResp.StatusCode);

        using var otherClientReq = new HttpRequestMessage(HttpMethod.Post, "/analyzer/api/analyze")
        {
            Content = new StringContent("""{"sql":"SELECT 1;"}""", Encoding.UTF8, "application/json"),
        };
        otherClientReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
        var otherClientResp = await host.Client.SendAsync(otherClientReq);
        Assert.Equal(HttpStatusCode.OK, otherClientResp.StatusCode);
    }

    [Fact]
    public async Task TuneEndpoint_WhenUpstreamFails_ReturnsSanitized502WithoutLeakingUpstreamDetails()
    {
        var config = new Dictionary<string, string?>
        {
            ["HERMES_TUNING_ENDPOINT"] = "https://gateway.example.local/v1",
            ["HERMES_TUNING_API_KEY"] = "test-key",
        };
        var stub = new StubAdvisorClient(
            new TuningResponse(false, string.Empty, "HTTP 500: internal-host:5000 stacktrace secret-detail"));

        await using var host = await StartTestHostAsync(config, advisorClient: stub);

        using var body = new StringContent("""{"sql":"SELECT Id FROM dbo.Orders;"}""", Encoding.UTF8, "application/json");
        var resp = await host.Client.PostAsync("/analyzer/api/tune", body);

        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        using var json = await ReadJsonAsync(resp);
        var error = json.RootElement.GetProperty("error").GetString();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("internal-host", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("5000", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stacktrace", error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-detail", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TuneEndpoint_WithMalformedEndpointConfig_Returns503()
    {
        var config = new Dictionary<string, string?>
        {
            ["HERMES_TUNING_ENDPOINT"] = "file:///etc/passwd",
            ["HERMES_TUNING_API_KEY"] = "test-key",
        };

        await using var host = await StartTestHostAsync(config);

        using var body = new StringContent("""{"sql":"SELECT Id FROM dbo.Orders;"}""", Encoding.UTF8, "application/json");
        var resp = await host.Client.PostAsync("/analyzer/api/tune", body);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Equal("application/json", resp.Content.Headers.ContentType?.MediaType);
        using var json = await ReadJsonAsync(resp);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Cors_AllowsConfiguredOriginIncludingOnErrors_AndBlocksDisallowedOrigin()
    {
        var config = new Dictionary<string, string?>
        {
            ["ALLOWED_ORIGINS"] = "https://rhol-ai.tech",
        };
        await using var host = await StartTestHostAsync(config);

        using var allowedReq = new HttpRequestMessage(HttpMethod.Post, "/analyzer/api/analyze")
        {
            Content = new StringContent("""{"sql":""}""", Encoding.UTF8, "application/json"),
        };
        allowedReq.Headers.Add("Origin", "https://rhol-ai.tech");
        var allowedResp = await host.Client.SendAsync(allowedReq);
        Assert.Equal(HttpStatusCode.BadRequest, allowedResp.StatusCode);
        Assert.Equal("https://rhol-ai.tech", Assert.Single(allowedResp.Headers.GetValues("Access-Control-Allow-Origin")));

        using var disallowedReq = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        disallowedReq.Headers.Add("Origin", "https://evil.example");
        var disallowedResp = await host.Client.SendAsync(disallowedReq);
        Assert.Equal(HttpStatusCode.OK, disallowedResp.StatusCode);
        Assert.False(disallowedResp.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task UnknownApiPaths_ReturnJson404_WhileStaticSpaUnderAnalyzerServesHtmlAndAssets()
    {
        var tempRoot = Directory.CreateTempSubdirectory("qanalyzer-web-root-");
        try
        {
            var assetsDir = Path.Combine(tempRoot.FullName, "wwwroot", "assets");
            Directory.CreateDirectory(assetsDir);
            await File.WriteAllTextAsync(
                Path.Combine(tempRoot.FullName, "wwwroot", "index.html"),
                "<!doctype html><html><body><div id=\"root\"></div></body></html>");
            await File.WriteAllTextAsync(
                Path.Combine(assetsDir, "app.js"),
                "console.log('ok');");

            await using var host = await StartTestHostAsync(contentRootPath: tempRoot.FullName);

            var indexResp = await host.Client.GetAsync("/analyzer/");
            Assert.Equal(HttpStatusCode.OK, indexResp.StatusCode);
            Assert.Equal("text/html", indexResp.Content.Headers.ContentType?.MediaType);
            Assert.Contains("<div id=\"root\">", await indexResp.Content.ReadAsStringAsync());

            var assetResp = await host.Client.GetAsync("/analyzer/assets/app.js");
            Assert.Equal(HttpStatusCode.OK, assetResp.StatusCode);
            Assert.Contains("console.log('ok');", await assetResp.Content.ReadAsStringAsync());

            foreach (var missingApiPath in new[] { "/api/nonexistent", "/analyzer/api/nonexistent" })
            {
                var notFoundResp = await host.Client.GetAsync(missingApiPath);
                Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);
                Assert.Equal("application/json", notFoundResp.Content.Headers.ContentType?.MediaType);
                using var json = await ReadJsonAsync(notFoundResp);
                Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("error").GetString()));
            }
        }
        finally
        {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task AnalyzeAndTuneEndpoints_RejectNonShowPlanXmlWith400_AndSurfaceNewPlanWarnings()
    {
        const string planWithWarnings = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp NodeId="1" PhysicalOp="Nested Loops" LogicalOp="Inner Join">
                <Warnings NoJoinPredicate="true">
                  <PlanAffectingConvert ConvertIssue="Seek Plan" Expression="CONVERT_IMPLICIT(nvarchar(20),[dbo].[Orders].[Code],0)" />
                  <ColumnsWithNoStatistics>
                    <ColumnReference Schema="[dbo]" Table="[Orders]" Column="[Status]" />
                  </ColumnsWithNoStatistics>
                </Warnings>
              </RelOp>
            </ShowPlanXML>
            """;

        var config = new Dictionary<string, string?>
        {
            ["HERMES_TUNING_ENDPOINT"] = "https://gateway.example.local/v1",
            ["HERMES_TUNING_API_KEY"] = "test-key",
        };
        var stub = new StubAdvisorClient(new TuningResponse(true, "Tambahkan predicate ON dan update statistik.", null));
        await using var host = await StartTestHostAsync(config, advisorClient: stub);

        var analyzePayload = JsonSerializer.Serialize(new
        {
            sql = "",
            sqlPlanXml = planWithWarnings,
        });
        using (var body = new StringContent(analyzePayload, Encoding.UTF8, "application/json"))
        {
            var resp = await host.Client.PostAsync("/analyzer/api/analyze", body);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var json = await ReadJsonAsync(resp);
            Assert.True(json.RootElement.GetProperty("hasCritical").GetBoolean());
            Assert.Equal(3, json.RootElement.GetProperty("planDiagnosticCount").GetInt32());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(diagnostics, d => d.GetProperty("ruleId").GetString() == ExecutionPlanParser.RuleImplicitConvert);
            Assert.Contains(diagnostics, d => d.GetProperty("ruleId").GetString() == ExecutionPlanParser.RuleNoJoinPredicate);
            Assert.Contains(diagnostics, d => d.GetProperty("ruleId").GetString() == ExecutionPlanParser.RuleMissingStatistics);
        }

        var nonShowPlanPayload = JsonSerializer.Serialize(new
        {
            sql = "SELECT Id FROM dbo.Orders;",
            sqlPlanXml = "<configuration><item /></configuration>",
        });

        foreach (var path in new[] { "/analyzer/api/analyze", "/analyzer/api/tune" })
        {
            using var badBody = new StringContent(nonShowPlanPayload, Encoding.UTF8, "application/json");
            var badResp = await host.Client.PostAsync(path, badBody);
            Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
            Assert.Equal("application/json", badResp.Content.Headers.ContentType?.MediaType);
            using var errJson = await ReadJsonAsync(badResp);
            var error = errJson.RootElement.GetProperty("error").GetString();
            Assert.False(string.IsNullOrWhiteSpace(error));
            Assert.Contains("ShowPlanXML", error, StringComparison.Ordinal);
            Assert.DoesNotContain("configuration", error, StringComparison.OrdinalIgnoreCase);
        }
    }
}
