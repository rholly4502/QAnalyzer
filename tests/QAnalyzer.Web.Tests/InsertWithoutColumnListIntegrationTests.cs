using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class InsertWithoutColumnListIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsInsertWithoutColumns_AndAllowsExplicitColumnLists()
    {
        var service = new AnalyzerService();

        var missingColumns = service.Analyze(new AnalyzeRequest
        {
            Sql = "INSERT INTO dbo.Orders VALUES (1, 'A');",
        });

        Assert.False(missingColumns.HasCritical);
        var insertDiag = Assert.Single(missingColumns.Diagnostics, d => d.RuleId == "INSERT_WITHOUT_COLUMNS");
        Assert.Equal("Warning", insertDiag.Level);

        var mergeMissingColumns = service.Analyze(new AnalyzeRequest
        {
            Sql = "MERGE dbo.Orders AS t USING dbo.Stage AS s ON t.Id = s.Id WHEN NOT MATCHED THEN INSERT VALUES (s.Id, s.Name);",
        });

        var mergeDiag = Assert.Single(mergeMissingColumns.Diagnostics, d => d.RuleId == "INSERT_WITHOUT_COLUMNS");
        Assert.Equal("Warning", mergeDiag.Level);

        var explicitColumns = service.Analyze(new AnalyzeRequest
        {
            Sql = "INSERT INTO dbo.Orders (Id, Name) VALUES (1, 'A');",
        });

        Assert.Equal(0, explicitColumns.Total);
        Assert.False(explicitColumns.HasCritical);
    }

    [Fact]
    public void AnalyzerService_Analyze_ReportingProfile_PreservesInsertWithoutColumnsWarning()
    {
        var service = new AnalyzerService();

        var resp = service.Analyze(new AnalyzeRequest
        {
            Sql = "INSERT INTO dbo.Orders SELECT Id, Name FROM dbo.StagingOrders;",
            Profile = "reporting",
        });

        var diag = Assert.Single(resp.Diagnostics, d => d.RuleId == "INSERT_WITHOUT_COLUMNS");
        Assert.Equal("Warning", diag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsInsertWithoutColumnsDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Tambahkan daftar kolom eksplisit pada INSERT.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "INSERT INTO dbo.Orders VALUES (1, 'A');",
        });

        Assert.True(resp.Success);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "INSERT_WITHOUT_COLUMNS" && d.Level == "Warning");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "INSERT_WITHOUT_COLUMNS");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_InsertWithoutColumns_Returns200WithDiagnostic(string path)
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
                """{"sql":"INSERT INTO dbo.Orders VALUES (1, 'A');"}""",
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "INSERT_WITHOUT_COLUMNS"
                     && d.GetProperty("level").GetString() == "Warning");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
