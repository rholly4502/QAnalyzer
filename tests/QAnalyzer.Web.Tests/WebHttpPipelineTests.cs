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
}
