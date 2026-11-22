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

public class ExecutionPlanAdvancedIntegrationTests
{
    private const string ShowPlanNs = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    private sealed class CapturingAdvisorClient(TuningResponse response) : ITuningAdvisorClient
    {
        public TuningRequest? LastRequest { get; private set; }

        public Task<TuningResponse> AskAsync(TuningRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private static string BuildAdvancedPlanXml() => $"""
        <ShowPlanXML xmlns="{ShowPlanNs}">
          <BatchSequence>
            <Batch>
              <Statements>
                <StmtSimple StatementOptmEarlyAbortReason="TimeOut">
                  <QueryPlan>
                    <Warnings>
                      <MemoryGrantWarning GrantWarningKind="Excessive Grant" RequestedMemory="65536" GrantedMemory="65536" MaxUsedMemory="512" />
                      <UnmatchedIndexes>
                        <Parameterization>
                          <Object Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Open]" />
                        </Parameterization>
                      </UnmatchedIndexes>
                    </Warnings>
                    <RelOp NodeId="1" PhysicalOp="Clustered Index Seek" LogicalOp="Clustered Index Seek" EstimateRows="5" Parallel="false">
                      <RunTimeInformation>
                        <RunTimeCountersPerThread Thread="0" ActualRows="25000" ActualExecutions="1" />
                      </RunTimeInformation>
                    </RelOp>
                    <RelOp NodeId="2" PhysicalOp="Parallelism" LogicalOp="Repartition Streams">
                      <Warnings>
                        <ExchangeSpillDetails WritesToTempDb="18" />
                      </Warnings>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements>
            </Batch>
          </BatchSequence>
        </ShowPlanXML>
        """;

    [Fact]
    public void AnalyzerService_Analyze_SurfacesAdvancedPlanDiagnosticsForPlanOnlyAndCombined()
    {
        var service = new AnalyzerService();
        var planXml = BuildAdvancedPlanXml();

        var planOnly = service.Analyze(new AnalyzeRequest
        {
            Sql = "",
            SqlPlanXml = planXml,
        });

        Assert.True(planOnly.HasExecutionPlan);
        Assert.Equal(5, planOnly.PlanDiagnosticCount);
        Assert.Equal(5, planOnly.Total);
        Assert.True(planOnly.HasCritical);
        Assert.Contains(planOnly.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleMemoryGrantWarning && d.Level == "Warning");
        Assert.Contains(planOnly.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleUnmatchedIndex && d.Level == "Warning");
        Assert.Contains(planOnly.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleOptimizerEarlyAbort && d.Level == "Warning");
        Assert.Contains(planOnly.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew && d.Level == "Warning");
        Assert.Contains(planOnly.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb && d.Level == "Critical");

        var combined = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT * FROM dbo.Orders;",
            SqlPlanXml = planXml,
        });

        Assert.True(combined.HasExecutionPlan);
        Assert.Equal(5, combined.PlanDiagnosticCount);
        Assert.Equal(6, combined.Total);
        Assert.Contains(combined.Diagnostics, d => d.RuleId == "SELECT_STAR");
        Assert.Contains(combined.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsAdvancedPlanDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Perbarui statistik dan pertimbangkan OPTION (RECOMPILE).", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT Id FROM dbo.Orders;",
            SqlPlanXml = BuildAdvancedPlanXml(),
        });

        Assert.True(resp.Success);
        Assert.True(resp.HasCritical);
        Assert.Equal(5, resp.TotalDiagnostics);
        Assert.NotNull(client.LastRequest);
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleMemoryGrantWarning);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleUnmatchedIndex);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleOptimizerEarlyAbort);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleEstimateSkew);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == ExecutionPlanParser.RuleSpillToTempDb);
    }

    [Fact]
    public async Task HttpEndpoints_AnalyzeAndTune_SurfaceAdvancedPlanDiagnosticsAndIgnoreCleanActualPlan()
    {
        var config = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HERMES_TUNING_ENDPOINT"] = "https://gateway.example.local/v1",
            ["HERMES_TUNING_API_KEY"] = "test-key",
        };
        var stub = new CapturingAdvisorClient(new TuningResponse(true, "Saran tuning diterima.", null));

        var builder = Program.CreateAppBuilder(
            environmentName: Environments.Production,
            configurationOverrides: config);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITuningAdvisorClient>(stub);

        var app = Program.BuildApp(builder);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var planXml = BuildAdvancedPlanXml();

            var analyzePayload = JsonSerializer.Serialize(new
            {
                sql = "",
                sqlPlanXml = planXml,
            });

            foreach (var path in new[] { "/api/analyze", "/analyzer/api/analyze" })
            {
                using var body = new StringContent(analyzePayload, Encoding.UTF8, "application/json");
                var resp = await client.PostAsync(path, body);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                Assert.True(json.RootElement.GetProperty("hasExecutionPlan").GetBoolean());
                Assert.Equal(5, json.RootElement.GetProperty("planDiagnosticCount").GetInt32());
                Assert.True(json.RootElement.GetProperty("hasCritical").GetBoolean());

                var ruleIds = json.RootElement
                    .GetProperty("diagnostics")
                    .EnumerateArray()
                    .Select(d => d.GetProperty("ruleId").GetString())
                    .ToList();
                Assert.Contains(ExecutionPlanParser.RuleMemoryGrantWarning, ruleIds);
                Assert.Contains(ExecutionPlanParser.RuleUnmatchedIndex, ruleIds);
                Assert.Contains(ExecutionPlanParser.RuleOptimizerEarlyAbort, ruleIds);
                Assert.Contains(ExecutionPlanParser.RuleEstimateSkew, ruleIds);
                Assert.Contains(ExecutionPlanParser.RuleSpillToTempDb, ruleIds);
            }

            var tunePayload = JsonSerializer.Serialize(new
            {
                sql = "SELECT Id FROM dbo.Orders;",
                sqlPlanXml = planXml,
            });
            using (var tuneBody = new StringContent(tunePayload, Encoding.UTF8, "application/json"))
            {
                var tuneResp = await client.PostAsync("/analyzer/api/tune", tuneBody);
                Assert.Equal(HttpStatusCode.OK, tuneResp.StatusCode);
                using var tuneJson = JsonDocument.Parse(await tuneResp.Content.ReadAsStringAsync());
                Assert.True(tuneJson.RootElement.GetProperty("success").GetBoolean());
                Assert.Equal(5, tuneJson.RootElement.GetProperty("totalDiagnostics").GetInt32());
            }

            var cleanPlanXml = $"""
                <ShowPlanXML xmlns="{ShowPlanNs}">
                  <BatchSequence>
                    <Batch>
                      <Statements>
                        <StmtSimple StatementOptmLevel="FULL" StatementOptmEarlyAbortReason="GoodEnoughPlanFound">
                          <QueryPlan>
                            <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="100" Parallel="false">
                              <IndexScan Ordered="true">
                                <Object Schema="[dbo]" Table="[Orders]" Index="[IX_Orders_Id]" />
                              </IndexScan>
                              <RunTimeInformation>
                                <RunTimeCountersPerThread Thread="0" ActualRows="120" ActualExecutions="1" />
                              </RunTimeInformation>
                            </RelOp>
                          </QueryPlan>
                        </StmtSimple>
                      </Statements>
                    </Batch>
                  </BatchSequence>
                </ShowPlanXML>
                """;
            var cleanPayload = JsonSerializer.Serialize(new
            {
                sql = "",
                sqlPlanXml = cleanPlanXml,
            });
            using var cleanBody = new StringContent(cleanPayload, Encoding.UTF8, "application/json");
            var cleanResp = await client.PostAsync("/analyzer/api/analyze", cleanBody);
            Assert.Equal(HttpStatusCode.OK, cleanResp.StatusCode);
            using var cleanJson = JsonDocument.Parse(await cleanResp.Content.ReadAsStringAsync());
            Assert.True(cleanJson.RootElement.GetProperty("hasExecutionPlan").GetBoolean());
            Assert.Equal(0, cleanJson.RootElement.GetProperty("planDiagnosticCount").GetInt32());
            Assert.Equal(0, cleanJson.RootElement.GetProperty("total").GetInt32());
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
