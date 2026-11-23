using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class TopWithoutOrderByIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsTopWithoutOrderBy_AndAllowsOrderedTop()
    {
        var service = new AnalyzerService();

        var unordered = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT TOP (10) Id, CustomerId FROM dbo.Orders;",
        });

        Assert.False(unordered.HasCritical);
        var topDiag = Assert.Single(unordered.Diagnostics, d => d.RuleId == "TOP_WITHOUT_ORDER_BY");
        Assert.Equal("Warning", topDiag.Level);

        var viewTop100 = service.Analyze(new AnalyzeRequest
        {
            Sql = "CREATE VIEW dbo.vOrders AS SELECT TOP (100) PERCENT Id FROM dbo.Orders ORDER BY Id;",
        });

        var viewDiag = Assert.Single(viewTop100.Diagnostics, d => d.RuleId == "TOP_WITHOUT_ORDER_BY");
        Assert.Equal("Warning", viewDiag.Level);

        var ordered = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT TOP (10) Id, CustomerId FROM dbo.Orders ORDER BY CreatedAt DESC;",
        });

        Assert.Equal(0, ordered.Total);
        Assert.False(ordered.HasCritical);
    }

    [Fact]
    public void AnalyzerService_Analyze_ReportingProfile_PreservesTopWithoutOrderByWarning()
    {
        var service = new AnalyzerService();

        var resp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT TOP (5) Id FROM dbo.Orders;",
            Profile = "reporting",
        });

        var diag = Assert.Single(resp.Diagnostics, d => d.RuleId == "TOP_WITHOUT_ORDER_BY");
        Assert.Equal("Warning", diag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsTopWithoutOrderByDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Tambahkan ORDER BY CreatedAt DESC.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT TOP (10) Id FROM dbo.Orders;",
        });

        Assert.True(resp.Success);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "TOP_WITHOUT_ORDER_BY" && d.Level == "Warning");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "TOP_WITHOUT_ORDER_BY");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_TopWithoutOrderBy_Returns200WithDiagnostic(string path)
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
                """{"sql":"SELECT TOP (10) Id FROM dbo.Orders;"}""",
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "TOP_WITHOUT_ORDER_BY"
                     && d.GetProperty("level").GetString() == "Warning");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
