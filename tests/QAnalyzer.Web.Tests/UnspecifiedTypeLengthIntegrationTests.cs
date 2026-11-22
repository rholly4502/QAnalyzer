using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class UnspecifiedTypeLengthIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsUnspecifiedTypeLengths_AndAllowsExplicitLengths()
    {
        var service = new AnalyzerService();

        var critical = service.Analyze(new AnalyzeRequest
        {
            Sql = "DECLARE @name VARCHAR;",
        });

        Assert.True(critical.HasCritical);
        var criticalDiag = Assert.Single(critical.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH");
        Assert.Equal("Critical", criticalDiag.Level);

        var warningCast = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT CAST(Name AS NVARCHAR) FROM dbo.Orders;",
        });

        Assert.False(warningCast.HasCritical);
        var castDiag = Assert.Single(warningCast.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH");
        Assert.Equal("Warning", castDiag.Level);

        var warningDecimal = service.Analyze(new AnalyzeRequest
        {
            Sql = "DECLARE @price DECIMAL;",
        });

        Assert.False(warningDecimal.HasCritical);
        var decDiag = Assert.Single(warningDecimal.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH");
        Assert.Equal("Warning", decDiag.Level);

        var safe = service.Analyze(new AnalyzeRequest
        {
            Sql = "DECLARE @name VARCHAR(50); SELECT CAST(@name AS NVARCHAR(100));",
        });

        Assert.Equal(0, safe.Total);
        Assert.False(safe.HasCritical);
    }

    [Fact]
    public void AnalyzerService_Analyze_ReportingProfile_PreservesUnspecifiedTypeLengthSeverity()
    {
        var service = new AnalyzerService();

        var criticalResp = service.Analyze(new AnalyzeRequest
        {
            Sql = "DECLARE @name VARCHAR;",
            Profile = "reporting",
        });

        Assert.True(criticalResp.HasCritical);
        var criticalDiag = Assert.Single(criticalResp.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH");
        Assert.Equal("Critical", criticalDiag.Level);

        var warningResp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT CAST(Name AS NVARCHAR) FROM dbo.Orders;",
            Profile = "reporting",
        });

        Assert.False(warningResp.HasCritical);
        var warningDiag = Assert.Single(warningResp.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH");
        Assert.Equal("Warning", warningDiag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsUnspecifiedTypeLengthDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Gunakan VARCHAR(50) atau VARCHAR(MAX).", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "DECLARE @name VARCHAR;",
        });

        Assert.True(resp.Success);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH" && d.Level == "Critical");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "UNSPECIFIED_TYPE_LENGTH");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_UnspecifiedTypeLength_Returns200WithDiagnostic(string path)
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
                """{"sql":"DECLARE @name VARCHAR;"}""",
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("hasCritical").GetBoolean());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "UNSPECIFIED_TYPE_LENGTH"
                     && d.GetProperty("level").GetString() == "Critical");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
