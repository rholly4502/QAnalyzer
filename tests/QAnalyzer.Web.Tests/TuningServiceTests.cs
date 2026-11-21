using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using QAnalyzer.Core;
using QAnalyzer.Web.Security;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class TuningServiceTests
{
    private sealed class CapturingAdvisorClient(TuningResponse response) : ITuningAdvisorClient
    {
        public TuningRequest? LastRequest { get; private set; }
        public CancellationToken LastToken { get; private set; }

        public Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            LastToken = ct;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public void FromConfiguration_ReadsEnvVarKeys_AndSetsIsConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HERMES_TUNING_ENDPOINT"] = "https://gateway.example.local/v1",
                ["HERMES_TUNING_API_KEY"] = "test-key-from-env",
            })
            .Build();

        var opts = TuningGatewayOptions.FromConfiguration(config);

        Assert.True(opts.IsConfigured);
        Assert.Equal("https://gateway.example.local/v1", opts.Endpoint);
        Assert.Equal("test-key-from-env", opts.ApiKey);
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("https://gateway.example.local/v1", null)]
    [InlineData("", "")]
    public void FromConfiguration_WhenMissingEndpointOrKey_IsNotConfigured(string? endpoint, string? key)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HERMES_TUNING_ENDPOINT"] = endpoint,
                ["HERMES_TUNING_API_KEY"] = key,
            })
            .Build();

        var opts = TuningGatewayOptions.FromConfiguration(config);

        Assert.False(opts.IsConfigured);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://gw.internal/v1")]
    [InlineData("https://user:pass@gw.internal/v1")]
    [InlineData("https://gw.internal/v1?x=1")]
    [InlineData("https://gw.internal/v1#frag")]
    [InlineData("not-a-uri")]
    public void FromConfiguration_RejectsInvalidOrNonHttpEndpoints(string invalidEndpoint)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HERMES_TUNING_ENDPOINT"] = invalidEndpoint,
                ["HERMES_TUNING_API_KEY"] = "test-key",
            })
            .Build();

        var opts = TuningGatewayOptions.FromConfiguration(config);

        Assert.False(opts.IsConfigured);
        Assert.Equal(string.Empty, opts.Endpoint);
        Assert.False(new TuningGatewayOptions { Endpoint = invalidEndpoint, ApiKey = "test-key" }.IsConfigured);
    }

    [Fact]
    public void FromConfiguration_TrimsTrailingSlashOnValidEndpoint()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HERMES_TUNING_ENDPOINT"] = "https://gateway.example.local/v1/",
                ["HERMES_TUNING_API_KEY"] = "test-key",
            })
            .Build();

        var opts = TuningGatewayOptions.FromConfiguration(config);

        Assert.True(opts.IsConfigured);
        Assert.Equal("https://gateway.example.local/v1", opts.Endpoint);
    }

    [Fact]
    public void ValidateTuneRequest_EmptySql_Returns400()
    {
        var failure = WebSecurityValidator.ValidateTuneRequest(new TuneRequest { Sql = "   " }, 1000, 1000);

        Assert.NotNull(failure);
        Assert.Equal(StatusCodes.Status400BadRequest, failure.StatusCode);
    }

    [Fact]
    public void ValidateTuneRequest_SqlExceedsLimit_Returns413()
    {
        var failure = WebSecurityValidator.ValidateTuneRequest(
            new TuneRequest { Sql = new string('X', 101) }, 100, 1000);

        Assert.NotNull(failure);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, failure.StatusCode);
    }

    [Fact]
    public void ValidateTuneRequest_SqlPlanExceedsLimit_Returns413()
    {
        var failure = WebSecurityValidator.ValidateTuneRequest(
            new TuneRequest { Sql = "SELECT 1;", SqlPlanXml = new string('P', 201) }, 1000, 200);

        Assert.NotNull(failure);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, failure.StatusCode);
    }

    [Fact]
    public async Task TuneAsync_CombinesStaticAndExecutionPlanDiagnostics_AndForwardsCancellationToken()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Tambahkan index pada dbo.Orders.", null));
        var service = new TuningService(client);
        using var cts = new CancellationTokenSource();

        const string planXml = """
            <ShowPlanXML>
              <RelOp NodeId="1" PhysicalOp="Table Scan" LogicalOp="Table Scan">
                <TableScan>
                  <Object Schema="[dbo]" Table="[Orders]" />
                </TableScan>
              </RelOp>
            </ShowPlanXML>
            """;

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT * FROM dbo.Orders;",
            SqlPlanXml = planXml,
        }, cts.Token);

        Assert.True(resp.Success);
        Assert.Equal("Tambahkan index pada dbo.Orders.", resp.Content);
        Assert.Equal(cts.Token, client.LastToken);
        Assert.NotNull(client.LastRequest);
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "SELECT_STAR");
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleTableScan);
        Assert.Equal(resp.Diagnostics.Count, resp.TotalDiagnostics);
    }

    [Fact]
    public async Task TuneAsync_InvalidSqlPlanXml_ThrowsFormatException()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "ok", null));
        var service = new TuningService(client);

        await Assert.ThrowsAsync<FormatException>(() => service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT Id FROM dbo.Orders;",
            SqlPlanXml = "<ShowPlanXML><broken></ShowPlanXML>",
        }));
    }

    [Fact]
    public async Task TuneAsync_WhenAdvisorFails_ReturnsUnsuccessfulResponse()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(false, string.Empty, "HTTP 502: Upstream error"));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "UPDATE dbo.Orders SET Status = 'X';",
        });

        Assert.False(resp.Success);
        Assert.Equal("HTTP 502: Upstream error", resp.Error);
        Assert.True(resp.HasCritical);
    }

    [Fact]
    public async Task TuneAsync_ForwardsNewPlanWarningsAndRejectsNonShowPlanXml()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Perbaiki join dan statistik.", null));
        var service = new TuningService(client);

        const string planXml = """
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

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT Id FROM dbo.Orders;",
            SqlPlanXml = planXml,
        });

        Assert.True(resp.Success);
        Assert.True(resp.HasCritical);
        Assert.NotNull(client.LastRequest);
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleImplicitConvert);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleNoJoinPredicate);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleMissingStatistics);

        await Assert.ThrowsAsync<FormatException>(() => service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT Id FROM dbo.Orders;",
            SqlPlanXml = "<root><item /></root>",
        }));
    }
}
