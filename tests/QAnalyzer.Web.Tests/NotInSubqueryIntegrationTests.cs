using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class NotInSubqueryIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsNullableSubqueryAndExplicitNull_AndAllowsNullGuardedAndNotExists()
    {
        var service = new AnalyzerService();

        var warning = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);",
        });

        Assert.False(warning.HasCritical);
        var warningDiag = Assert.Single(warning.Diagnostics, d => d.RuleId == "NOT_IN_SUBQUERY");
        Assert.Equal("Warning", warningDiag.Level);

        var critical = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE Status NOT IN ('A', NULL);",
        });

        Assert.True(critical.HasCritical);
        var criticalDiag = Assert.Single(critical.Diagnostics, d => d.RuleId == "NOT_IN_SUBQUERY");
        Assert.Equal("Critical", criticalDiag.Level);

        var guarded = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist WHERE CustomerId IS NOT NULL);",
        });

        Assert.Equal(0, guarded.Total);
        Assert.False(guarded.HasCritical);

        var notExists = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders o WHERE NOT EXISTS (SELECT 1 FROM dbo.Blacklist b WHERE b.CustomerId = o.CustomerId);",
        });

        Assert.Equal(0, notExists.Total);
        Assert.False(notExists.HasCritical);
    }

    [Fact]
    public void AnalyzerService_Analyze_ReportingProfile_KeepsNotInSubquerySeverity()
    {
        var service = new AnalyzerService();

        var warningResp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);",
            Profile = "reporting",
        });

        Assert.False(warningResp.HasCritical);
        var warningDiag = Assert.Single(warningResp.Diagnostics, d => d.RuleId == "NOT_IN_SUBQUERY");
        Assert.Equal("Warning", warningDiag.Level);

        var criticalResp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE Status NOT IN ('A', NULL);",
            Profile = "reporting",
        });

        Assert.True(criticalResp.HasCritical);
        var criticalDiag = Assert.Single(criticalResp.Diagnostics, d => d.RuleId == "NOT_IN_SUBQUERY");
        Assert.Equal("Critical", criticalDiag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsNotInSubqueryDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Ganti NOT IN dengan NOT EXISTS.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);",
        });

        Assert.True(resp.Success);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "NOT_IN_SUBQUERY" && d.Level == "Warning");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "NOT_IN_SUBQUERY");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_NotInSubquery_Returns200WithDiagnostic(string path)
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
                """{"sql":"SELECT Id FROM dbo.Orders WHERE CustomerId NOT IN (SELECT CustomerId FROM dbo.Blacklist);"}""",
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "NOT_IN_SUBQUERY"
                     && d.GetProperty("level").GetString() == "Warning");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
