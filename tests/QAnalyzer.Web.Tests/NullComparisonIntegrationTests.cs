using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class NullComparisonIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsNullComparisonVariants_AndAllowsSafeNullChecks()
    {
        var service = new AnalyzerService();

        var critical = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;",
        });

        Assert.True(critical.HasCritical);
        var criticalDiag = Assert.Single(critical.Diagnostics, d => d.RuleId == "NULL_COMPARISON");
        Assert.Equal("Critical", criticalDiag.Level);

        var warning = service.Analyze(new AnalyzeRequest
        {
            Sql = "SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;",
        });

        Assert.False(warning.HasCritical);
        var warningDiag = Assert.Single(warning.Diagnostics, d => d.RuleId == "NULL_COMPARISON");
        Assert.Equal("Warning", warningDiag.Level);

        var isNull = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE DeletedAt IS NULL;",
        });

        Assert.Equal(0, isNull.Total);
        Assert.False(isNull.HasCritical);

        var distinctFrom = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE DeletedAt IS DISTINCT FROM NULL;",
        });

        Assert.Equal(0, distinctFrom.Total);
        Assert.False(distinctFrom.HasCritical);
    }

    [Fact]
    public void AnalyzerService_Analyze_ReportingProfile_PreservesNullComparisonSeverity()
    {
        var service = new AnalyzerService();

        var criticalResp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;",
            Profile = "reporting",
        });

        Assert.True(criticalResp.HasCritical);
        var criticalDiag = Assert.Single(criticalResp.Diagnostics, d => d.RuleId == "NULL_COMPARISON");
        Assert.Equal("Critical", criticalDiag.Level);

        var warningResp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SET ANSI_NULLS OFF; SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;",
            Profile = "reporting",
        });

        Assert.False(warningResp.HasCritical);
        var warningDiag = Assert.Single(warningResp.Diagnostics, d => d.RuleId == "NULL_COMPARISON");
        Assert.Equal("Warning", warningDiag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsNullComparisonDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Gunakan DeletedAt IS NULL.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;",
        });

        Assert.True(resp.Success);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "NULL_COMPARISON" && d.Level == "Critical");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "NULL_COMPARISON");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_NullComparison_Returns200WithDiagnostic(string path)
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
                """{"sql":"SELECT Id FROM dbo.Orders WHERE DeletedAt = NULL;"}""",
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("hasCritical").GetBoolean());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "NULL_COMPARISON"
                     && d.GetProperty("level").GetString() == "Critical");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
