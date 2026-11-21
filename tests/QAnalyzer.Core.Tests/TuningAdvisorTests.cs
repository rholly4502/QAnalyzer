using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QAnalyzer.Core;

public sealed class TuningAdvisorTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((req, _) => Task.FromResult(respond(req)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => respond(request, ct);
    }

    private sealed class UnopenedStreamContent : HttpContent
    {
        public bool WasStreamAccessed { get; private set; }

        public UnopenedStreamContent(long declaredLength)
        {
            Headers.ContentLength = declaredLength;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasStreamAccessed = true;
            return Task.CompletedTask;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            WasStreamAccessed = true;
            return Task.CompletedTask;
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            WasStreamAccessed = true;
            return Task.FromResult<Stream>(new MemoryStream());
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            WasStreamAccessed = true;
            return Task.FromResult<Stream>(new MemoryStream());
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return Headers.ContentLength.HasValue;
        }
    }

    private sealed class ChunkedStreamContent(Stream stream) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context)
            => stream.CopyToAsync(target);

        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken cancellationToken)
            => stream.CopyToAsync(target, cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync()
            => Task.FromResult(stream);

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => Task.FromResult(stream);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class CountingPatternStream(int totalBytes, int chunkSize = 4096) : Stream
    {
        private int _position;

        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = totalBytes - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = Math.Min(Math.Min(count, chunkSize), remaining);
            Array.Fill(buffer, (byte)' ', offset, toRead);
            _position += toRead;
            BytesRead += toRead;
            return toRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = totalBytes - _position;
            if (remaining <= 0)
            {
                return ValueTask.FromResult(0);
            }

            var toRead = Math.Min(Math.Min(buffer.Length, chunkSize), remaining);
            buffer.Span[..toRead].Fill((byte)' ');
            _position += toRead;
            BytesRead += toRead;
            return ValueTask.FromResult(toRead);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpClient ClientReturning(string json) =>
        new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));

    [Fact]
    public void Constructor_NullArguments_ThrowsArgumentNullException()
    {
        using var http = ClientReturning("{}");

        Assert.Throws<ArgumentNullException>(() => new HermesTuningAdvisor(null!, "http://gw:5000/v1", "secret"));
        Assert.Throws<ArgumentNullException>(() => new HermesTuningAdvisor(http, null!, "secret"));
        Assert.Throws<ArgumentNullException>(() => new HermesTuningAdvisor(http, "http://gw:5000/v1", null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-uri")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://gw:5000/v1")]
    [InlineData("https://user:pass@gw/v1")]
    [InlineData("https://gw/v1?x=1")]
    [InlineData("https://gw/v1#frag")]
    [InlineData("https://gw/v1 extra")]
    public void Constructor_InvalidEndpoint_ThrowsArgumentException(string invalidEndpoint)
    {
        using var http = ClientReturning("{}");

        var ex = Assert.Throws<ArgumentException>(() => new HermesTuningAdvisor(http, invalidEndpoint, "secret"));
        Assert.Equal("endpoint", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("secret\r\nX-Injected: 1")]
    [InlineData("secret\nline")]
    [InlineData("secret key")]
    public void Constructor_InvalidApiKey_ThrowsArgumentException(string invalidApiKey)
    {
        using var http = ClientReturning("{}");

        var ex = Assert.Throws<ArgumentException>(() => new HermesTuningAdvisor(http, "http://gw:5000/v1", invalidApiKey));
        Assert.Equal("apiKey", ex.ParamName);
    }

    [Fact]
    public async Task AskAsync_NullRequest_ThrowsArgumentNullException()
    {
        using var http = ClientReturning("{}");
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        await Assert.ThrowsAsync<ArgumentNullException>(() => advisor.AskAsync(null!));
    }

    [Fact]
    public async Task AskAsync_NormalizesTrailingSlashAndSetsBearerAuthorizationHeader()
    {
        Uri? capturedUri = null;
        AuthenticationHeaderValue? capturedAuth = null;
        var json = """{"choices":[{"message":{"content":"Gunakan index."}}]}""";

        using var http = new HttpClient(new StubHandler(req =>
        {
            capturedUri = req.RequestUri;
            capturedAuth = req.Headers.Authorization;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        }));

        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1/", "secret-key_123");
        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));

        Assert.True(resp.Success);
        Assert.Equal(new Uri("http://gw:5000/v1/chat/completions"), capturedUri);
        Assert.NotNull(capturedAuth);
        Assert.Equal("Bearer", capturedAuth!.Scheme);
        Assert.Equal("secret-key_123", capturedAuth.Parameter);
    }

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

    [Fact]
    public async Task AskAsync_PreCanceledToken_ThrowsOperationCanceledException()
    {
        var handlerCalled = false;
        using var http = new HttpClient(new StubHandler(_ =>
        {
            handlerCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => advisor.AskAsync(new("SELECT 1;", [], [], null), cts.Token));
        Assert.False(handlerCalled);
    }

    [Fact]
    public async Task AskAsync_CanceledMidRequest_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new StubHandler((_, token) =>
        {
            cts.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => advisor.AskAsync(new("SELECT 1;", [], [], null), cts.Token));
    }

    [Fact]
    public async Task AskAsync_OnHttpTimeout_ReturnsTimeoutFailureResponse()
    {
        using var http = new HttpClient(new StubHandler((_, _) =>
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.")));
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null), CancellationToken.None);

        Assert.False(resp.Success);
        Assert.False(string.IsNullOrWhiteSpace(resp.Error));
        Assert.Contains("waktu", resp.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<html>error</html>")]
    [InlineData("{}")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{}]}""")]
    [InlineData("""{"choices":[{"message":{}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"   "}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":123}}]}""")]
    [InlineData("[]")]
    public async Task AskAsync_MalformedOrUnexpectedSuccessJson_ReturnsFailureWithoutThrowing(string body)
    {
        using var http = ClientReturning(body);
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));

        Assert.False(resp.Success);
        Assert.False(string.IsNullOrWhiteSpace(resp.Error));
    }

    [Fact]
    public async Task AskAsync_Utf8BomPrefixedJson_ReturnsSuccessContent()
    {
        var jsonBytes = Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"Gunakan index covering."}}]}""");
        var bomBytes = new byte[] { 0xEF, 0xBB, 0xBF };
        var payload = new byte[bomBytes.Length + jsonBytes.Length];
        bomBytes.CopyTo(payload, 0);
        jsonBytes.CopyTo(payload, bomBytes.Length);

        using var http = new HttpClient(new StubHandler(_ =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));

        Assert.True(resp.Success);
        Assert.Equal("Gunakan index covering.", resp.Content);
    }

    [Fact]
    public async Task AskAsync_ContentLengthExceedingMaxResponseBytes_RejectsBeforeReadingStream()
    {
        var content = new UnopenedStreamContent(HermesTuningAdvisor.MaxResponseBytes + 1);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
        }));
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));

        Assert.False(resp.Success);
        Assert.False(content.WasStreamAccessed);
        Assert.Contains(HermesTuningAdvisor.MaxResponseBytes.ToString(), resp.Error);
    }

    [Fact]
    public async Task AskAsync_ChunkedStreamExceedingMaxResponseBytes_AbortsEarlyAndFails()
    {
        var exactOverOneStream = new CountingPatternStream(HermesTuningAdvisor.MaxResponseBytes + 1, chunkSize: 4096);
        using var http1 = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedStreamContent(exactOverOneStream),
        }));
        var advisor1 = new HermesTuningAdvisor(http1, "http://gw:5000/v1", "secret");

        var resp1 = await advisor1.AskAsync(new("SELECT 1;", [], [], null));
        Assert.False(resp1.Success);
        Assert.Contains(HermesTuningAdvisor.MaxResponseBytes.ToString(), resp1.Error);

        var largeTotal = HermesTuningAdvisor.MaxResponseBytes + (1024 * 1024);
        var largeStream = new CountingPatternStream(largeTotal, chunkSize: 4096);
        using var http2 = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ChunkedStreamContent(largeStream),
        }));
        var advisor2 = new HermesTuningAdvisor(http2, "http://gw:5000/v1", "secret");

        var resp2 = await advisor2.AskAsync(new("SELECT 1;", [], [], null));
        Assert.False(resp2.Success);
        Assert.True(largeStream.BytesRead <= HermesTuningAdvisor.MaxResponseBytes + 16_384,
            $"Stream should abort early after exceeding limit, but read {largeStream.BytesRead} bytes.");
    }

    [Fact]
    public async Task AskAsync_ResponseAtExactMaxResponseBytes_Succeeds()
    {
        const string prefix = """{"choices":[{"message":{"content":"Exact limit OK."}}]}""";
        var prefixBytes = Encoding.UTF8.GetByteCount(prefix);
        var padded = prefix + new string(' ', HermesTuningAdvisor.MaxResponseBytes - prefixBytes);
        Assert.Equal(HermesTuningAdvisor.MaxResponseBytes, Encoding.UTF8.GetByteCount(padded));

        using var http = ClientReturning(padded);
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));

        Assert.True(resp.Success);
        Assert.Equal("Exact limit OK.", resp.Content);
    }

    [Fact]
    public async Task AskAsync_OversizedErrorResponse_IncludesHttpStatusCodeAndFails()
    {
        var oversizedError = new string('E', HermesTuningAdvisor.MaxResponseBytes + 10);
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(oversizedError, Encoding.UTF8, "text/plain"),
        }));
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));

        Assert.False(resp.Success);
        Assert.Contains("HTTP 500", resp.Error);
    }

    [Fact]
    public async Task AskAsync_RedirectStatus_ReturnsHttpStatusError()
    {
        using var http = new HttpClient(new StubHandler(_ =>
        {
            var msg = new HttpResponseMessage(HttpStatusCode.Found);
            msg.Headers.Location = new Uri("/internal-secret", UriKind.Relative);
            return msg;
        }));
        var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret");

        var resp = await advisor.AskAsync(new("SELECT 1;", [], [], null));

        Assert.False(resp.Success);
        Assert.Contains("HTTP 302", resp.Error);
    }

    [Fact]
    public async Task AskAsync_WithTuningMemory_SanitizesCrAndLfInLoggedSql()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"qanalyzer-mem-{Guid.NewGuid():N}.md");
        try
        {
            var memory = new TuningMemory(tempFile);
            var json = """{"choices":[{"message":{"content":"Saran ringkas"}}]}""";
            using var http = ClientReturning(json);
            var advisor = new HermesTuningAdvisor(http, "http://gw:5000/v1", "secret", memory);

            var resp = await advisor.AskAsync(new("SELECT 1\r\nFROM dbo.Orders", [], [], null));

            Assert.True(resp.Success);
            var written = File.ReadAllText(tempFile);
            var entryLine = Assert.Single(written.Split('\n', StringSplitOptions.RemoveEmptyEntries), l => l.StartsWith("- "));
            Assert.DoesNotContain("\r", entryLine);
            Assert.Contains("SELECT 1  FROM dbo.Orders", entryLine);
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }
}
