using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class SqlSyntaxErrorIntegrationTests
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
    public void AnalyzerService_Analyze_MalformedSqlWithMaxDop_FlagsCriticalAndKeepsOriginalSql()
    {
        const string malformedSql = "SELECT * FORM dbo.Orders;";
        var service = new AnalyzerService();

        var resp = service.Analyze(new AnalyzeRequest
        {
            Sql = malformedSql,
            EnableMaxDopTransform = true,
        });

        Assert.True(resp.HasCritical);
        var diag = Assert.Single(resp.Diagnostics, d => d.RuleId == "SQL_SYNTAX_ERROR");
        Assert.Equal("Critical", diag.Level);
        Assert.Equal(1, diag.Line);
        Assert.True(diag.Column > 0);
        Assert.Equal(malformedSql, resp.TransformedSql);
    }

    [Fact]
    public void AnalyzerService_Analyze_MalformedSqlInReportingProfile_KeepsCritical()
    {
        var service = new AnalyzerService();

        var resp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT * FORM dbo.Orders;",
            Profile = "reporting",
        });

        Assert.True(resp.HasCritical);
        var diag = Assert.Single(resp.Diagnostics, d => d.RuleId == "SQL_SYNTAX_ERROR");
        Assert.Equal("Critical", diag.Level);
    }

    [Fact]
    public void AnalyzerService_Analyze_ValidSql_DoesNotFlagSyntaxError()
    {
        var service = new AnalyzerService();

        var resp = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE Id = @id;",
        });

        Assert.Equal(0, resp.Total);
        Assert.False(resp.HasCritical);
        Assert.DoesNotContain(resp.Diagnostics, d => d.RuleId == "SQL_SYNTAX_ERROR");
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsSyntaxErrorDiagnosticWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Perbaiki typo FORM menjadi FROM.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT * FORM dbo.Orders;",
        });

        Assert.True(resp.Success);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "SQL_SYNTAX_ERROR" && d.Level == "Critical");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "SQL_SYNTAX_ERROR");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }

    [Theory]
    [InlineData("/api/analyze")]
    [InlineData("/analyzer/api/analyze")]
    public async Task AnalyzeEndpoint_MalformedSql_Returns200WithSyntaxErrorCriticalDiagnostic(string path)
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
            using var body = new StringContent("""{"sql":"SELECT * FORM dbo.Orders;"}""", Encoding.UTF8, "application/json");

            var response = await client.PostAsync(path, body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(json.RootElement.GetProperty("hasCritical").GetBoolean());
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToList();
            Assert.Contains(
                diagnostics,
                d => d.GetProperty("ruleId").GetString() == "SQL_SYNTAX_ERROR"
                     && d.GetProperty("level").GetString() == "Critical");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
