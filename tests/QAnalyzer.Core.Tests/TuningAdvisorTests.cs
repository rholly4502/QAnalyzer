using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QAnalyzer.Core;

public sealed class TuningAdvisorTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static HttpClient ClientReturning(string json) =>
        new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    [Fact]
    public async Task AskAsync_OnSuccess_ReturnsContent()
    {
        var json = """{"choices":[{"message":{"content":"Tambahkan index pada kolom CustomerId."}}]}""";
        var advisor = new HermesTuningAdvisor(ClientReturning(json), "http://gw:5000/v1", "secret");
        var resp = await advisor.AskAsync(new(
            "SELECT * FROM dbo.Orders WHERE CustomerId=@id;",
            [], [ new("db","dbo","Orders","CustomerId",null,null,1,0,90) ], null));
        Assert.True(resp.Success);
        Assert.Contains("index", resp.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AskAsync_OnNonSuccess_ReturnsError()
    {
        var advisor = new HermesTuningAdvisor(
            new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            { Content = new StringContent("bad") })), "http://gw:5000/v1", "secret");
        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));
        Assert.False(resp.Success);
        Assert.NotNull(resp.Error);
    }
}
