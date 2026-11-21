using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class NonSargableIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsNonSargableAndPassesSargable()
    {
        var service = new AnalyzerService();

        var nonSargable = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE YEAR(OrderDate) = 2025;",
        });

        var diag = Assert.Single(nonSargable.Diagnostics, d => d.RuleId == "NON_SARGABLE");
        Assert.Equal("Warning", diag.Level);

        var sargable = service.Analyze(new AnalyzeRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE OrderDate >= '2025-01-01';",
        });

        Assert.Equal(0, sargable.Total);
        Assert.Empty(sargable.Diagnostics);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsNonSargableDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Gunakan range predicate.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "SELECT Id FROM dbo.Orders WHERE YEAR(OrderDate) = 2025;",
        });

        Assert.True(resp.Success);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "NON_SARGABLE" && d.Level == "Warning");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "NON_SARGABLE");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }
}
