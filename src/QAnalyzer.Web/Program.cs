using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using QAnalyzer.Web.Security;
using QAnalyzer.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var securityOptions = WebSecurityOptions.FromConfiguration(builder.Configuration);
var tuningOptions = TuningGatewayOptions.FromConfiguration(builder.Configuration);
var allowedOrigins = WebSecurityOptions.ResolveAllowedOrigins(
    builder.Configuration,
    builder.Environment.IsDevelopment());

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = securityOptions.MaxRequestBodyBytes;
});

builder.Services.AddSingleton(securityOptions);
builder.Services.AddSingleton(tuningOptions);
builder.Services.AddSingleton<AnalyzerService>();
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
})
{
    Timeout = TimeSpan.FromSeconds(30),
});
builder.Services.AddSingleton<ITuningAdvisorClient, HermesTuningAdvisorClient>();
builder.Services.AddSingleton<TuningService>();

builder.Services.AddCors(options =>
{
    options.AddPolicy("QAnalyzerCors", policy =>
    {
        if (allowedOrigins.Length > 0)
        {
            policy.WithOrigins(allowedOrigins)
                .WithMethods("GET", "POST", "OPTIONS")
                .WithHeaders("Content-Type");
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { error = "Terlalu banyak permintaan. Coba lagi beberapa saat." },
            cancellationToken);
    };

    options.AddPolicy("analyze", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = securityOptions.RateLimitPermitLimit,
                Window = TimeSpan.FromSeconds(securityOptions.RateLimitWindowSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = securityOptions.RateLimitQueueLimit,
            }));

    var tunePermitLimit = Math.Max(1, Math.Min(10, securityOptions.RateLimitPermitLimit));
    options.AddPolicy("tune", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = tunePermitLimit,
                Window = TimeSpan.FromSeconds(securityOptions.RateLimitWindowSeconds),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = securityOptions.RateLimitQueueLimit,
            }));
});

var app = builder.Build();
app.UseCors("QAnalyzerCors");
app.UseRateLimiter();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/analyze", (AnalyzeRequest req, AnalyzerService svc, WebSecurityOptions opts) =>
{
    var failure = WebSecurityValidator.ValidateAnalyzeRequest(req, opts.MaxSqlLength);
    if (failure is not null)
    {
        return Results.Json(new { error = failure.Error }, statusCode: failure.StatusCode);
    }

    var resp = svc.Analyze(req);
    return Results.Ok(resp);
}).RequireRateLimiting("analyze");

app.MapPost("/api/tune", async (
    TuneRequest req,
    TuningService svc,
    WebSecurityOptions opts,
    TuningGatewayOptions tuningOpts,
    CancellationToken ct) =>
{
    var failure = WebSecurityValidator.ValidateTuneRequest(req, opts.MaxSqlLength, opts.MaxSqlLength);
    if (failure is not null)
    {
        return Results.Json(new { error = failure.Error }, statusCode: failure.StatusCode);
    }

    if (!tuningOpts.IsConfigured)
    {
        return Results.Json(
            new { error = "Layanan AI tuning belum dikonfigurasi di server." },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    try
    {
        var resp = await svc.TuneAsync(req, ct);
        if (!resp.Success)
        {
            return Results.Json(
                new { error = resp.Error ?? "Gagal menghubungi layanan AI tuning." },
                statusCode: StatusCodes.Status502BadGateway);
        }

        return Results.Ok(resp);
    }
    catch (FormatException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("tune");

// Sajikan frontend hasil build (Vite) dari wwwroot bila ada.
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions { RequestPath = "/analyzer" });
    app.MapFallbackToFile("index.html");
}

app.Run();

public partial class Program { }
