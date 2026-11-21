using QAnalyzer.Core;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Tests;

public class DestructiveRulesIntegrationTests
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
    public void AnalyzerService_Analyze_DetectsBareDeleteAndPassesDeleteWithWhere()
    {
        var service = new AnalyzerService();

        var bareDelete = service.Analyze(new AnalyzeRequest
        {
            Sql = "DELETE FROM dbo.Orders;",
        });

        Assert.True(bareDelete.HasCritical);
        var diag = Assert.Single(bareDelete.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE");
        Assert.Equal("Critical", diag.Level);

        var safeDelete = service.Analyze(new AnalyzeRequest
        {
            Sql = "DELETE FROM dbo.Orders WHERE Id = @id;",
        });

        Assert.Equal(0, safeDelete.Total);
        Assert.False(safeDelete.HasCritical);
    }

    [Fact]
    public void AnalyzerService_Analyze_DetectsTruncateTableAndKeepsCriticalInReportingProfile()
    {
        var service = new AnalyzerService();

        var general = service.Analyze(new AnalyzeRequest
        {
            Sql = "TRUNCATE TABLE dbo.Orders;",
        });

        Assert.True(general.HasCritical);
        var generalDiag = Assert.Single(general.Diagnostics, d => d.RuleId == "TRUNCATE_TABLE");
        Assert.Equal("Critical", generalDiag.Level);

        var reporting = service.Analyze(new AnalyzeRequest
        {
            Sql = "TRUNCATE TABLE dbo.Orders;",
            Profile = "reporting",
        });

        Assert.True(reporting.HasCritical);
        var reportingDiag = Assert.Single(reporting.Diagnostics, d => d.RuleId == "TRUNCATE_TABLE");
        Assert.Equal("Critical", reportingDiag.Level);
    }

    [Fact]
    public async Task TuningService_TuneAsync_ForwardsDeleteAndTruncateDiagnosticsWithoutDatabase()
    {
        var client = new CapturingAdvisorClient(new TuningResponse(true, "Tambahkan WHERE atau backup sebelum TRUNCATE.", null));
        var service = new TuningService(client);

        var resp = await service.TuneAsync(new TuneRequest
        {
            Sql = "DELETE FROM dbo.Orders;\nTRUNCATE TABLE dbo.AuditLog;",
        });

        Assert.True(resp.Success);
        Assert.True(resp.HasCritical);
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE" && d.Level == "Critical");
        Assert.Contains(resp.Diagnostics, d => d.RuleId == "TRUNCATE_TABLE" && d.Level == "Critical");
        Assert.NotNull(client.LastRequest);
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "DELETE_NO_WHERE");
        Assert.Contains(client.LastRequest.Diagnostics, d => d.RuleId == "TRUNCATE_TABLE");
        Assert.Empty(client.LastRequest.MissingIndexes);
        Assert.Null(client.LastRequest.CachedStat);
    }
}
