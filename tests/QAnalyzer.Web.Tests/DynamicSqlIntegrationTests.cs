using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class DynamicSqlIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsDynamicSqlCriticalAndWarning_AndPassesSafeSpExecuteSql()
    {
        var service = new AnalyzerService();

        var critical = service.Analyze(new AnalyzeRequest
        {
            Sql = "EXEC('SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + '''');",
        });

        Assert.True(critical.HasCritical);
        var criticalDiag = Assert.Single(critical.Diagnostics, d => d.RuleId == "DYNAMIC_SQL");
        Assert.Equal("Critical", criticalDiag.Level);

        var warning = service.Analyze(new AnalyzeRequest
        {
            Sql = "EXEC(@sql);",
        });

        Assert.False(warning.HasCritical);
        var warningDiag = Assert.Single(warning.Diagnostics, d => d.RuleId == "DYNAMIC_SQL");
        Assert.Equal("Warning", warningDiag.Level);

        var safe = service.Analyze(new AnalyzeRequest
        {
            Sql = "EXEC sp_executesql N'SELECT Id FROM dbo.Orders WHERE Id = @Id;', N'@Id INT', @Id = @id;",
        });

        Assert.Equal(0, safe.Total);
        Assert.False(safe.HasCritical);
        Assert.DoesNotContain(safe.Diagnostics, d => d.RuleId == "DYNAMIC_SQL");
    }

    [Fact]
    public void AnalyzerService_Analyze_ReportingProfile_KeepsDynamicSqlSeverity()
    {
        var service = new AnalyzerService();

        var resp = service.Analyze(new AnalyzeRequest
        {
            Sql = "EXEC('SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + '''');",
            Profile = "reporting",
        });

        Assert.True(resp.HasCritical);
        var diag = Assert.Single(resp.Diagnostics, d => d.RuleId == "DYNAMIC_SQL");
        Assert.Equal("Critical", diag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsDynamicSqlDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Gunakan sp_executesql dengan parameter.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "DECLARE @sql NVARCHAR(MAX) = N'SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + N''''; EXEC(@sql);",
        });

        Assert.True(resp.Success);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "DYNAMIC_SQL" && d.Level == "Critical");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "DYNAMIC_SQL");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_DynamicSqlConcatenation_Returns200WithCriticalDiagnostic(string path)
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
                """{"sql":"EXEC('SELECT Id FROM dbo.Orders WHERE Name = ''' + @name + '''');"}""",
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("hasCritical").GetBoolean());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "DYNAMIC_SQL"
                     && d.GetProperty("level").GetString() == "Critical");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
