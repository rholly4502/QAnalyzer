using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class CursorUsageIntegrationTests
{
    private sealed class CapturingAdvisorClient(TuningResponse response) : ITuningAdvisorClient
    {
        public TuningRequest? LastRequest { get; private set; }

        public Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void AnalyzerService_Analyze_DetectsCursorWarningAndInfo_AndPassesSetBasedQuery()
    {
        var service = new AnalyzerService();

        var warning = service.Analyze(new AnalyzeRequest
        {
            Sql = "DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders;",
        });

        Assert.False(warning.HasCritical);
        var warningDiag = Assert.Single(warning.Diagnostics, d => d.RuleId == "CURSOR_USAGE");
        Assert.Equal("Warning", warningDiag.Level);

        var info = service.Analyze(new AnalyzeRequest
        {
            Sql = "DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Id FROM dbo.Orders;",
        });

        Assert.False(info.HasCritical);
        var infoDiag = Assert.Single(info.Diagnostics, d => d.RuleId == "CURSOR_USAGE");
        Assert.Equal("Info", infoDiag.Level);

        var safe = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id, CustomerId FROM dbo.Orders WHERE Status = 1;",
        });

        Assert.Equal(0, safe.Total);
        Assert.False(safe.HasCritical);
        Assert.DoesNotContain(safe.Diagnostics, d => d.RuleId == "CURSOR_USAGE");
    }

    [Fact]
    public void AnalyzerService_Analyze_ReportingProfile_KeepsCursorUsageSeverity()
    {
        var service = new AnalyzerService();

        var resp = service.Analyze(new AnalyzeRequest
        {
            Sql = "DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders;",
            Profile = "reporting",
        });

        Assert.False(resp.HasCritical);
        var diag = Assert.Single(resp.Diagnostics, d => d.RuleId == "CURSOR_USAGE");
        Assert.Equal("Warning", diag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsCursorUsageDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Ganti cursor dengan operasi set-based.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders;",
        });

        Assert.True(resp.Success);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "CURSOR_USAGE" && d.Level == "Warning");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "CURSOR_USAGE");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_CursorUsage_Returns200WithDiagnostic(string path)
    {
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HERMES_TUNING_ENDPOINT"] = string.Empty,
            ["HERMES_TUNING_API_KEY"] = string.Empty,
            ["Tuning:Endpoint"] = string.Empty,
            ["Tuning:ApiKey"] = string.Empty,
        };

        var builder = Program.CreateAppBuilder(
            environmentName: Environments.Production,
            configurationOverrides: config);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = Program.BuildApp(builder);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var body = new StringContent(
                """{"sql":"DECLARE c CURSOR FOR SELECT Id FROM dbo.Orders;"}""",
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "CURSOR_USAGE"
                     && d.GetProperty("level").GetString() == "Warning");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
