using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using QAnalyzer.Web.Security;
using QAnalyzer.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var securityOptions = WebSecurityOptions.FromConfiguration(builder.Configuration);
var allowedOrigins = WebSecurityOptions.ResolveAllowedOrigins(
    builder.Configuration,
    builder.Environment.IsDevelopment());

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = securityOptions.MaxRequestBodyBytes;
});

builder.Services.AddSingleton(securityOptions);
builder.Services.AddSingleton<AnalyzerService>();

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

// Sajikan frontend hasil build (Vite) dari wwwroot bila ada.
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions { RequestPath = "/analyzer" });
    app.MapFallbackToFile("index.html");
}

app.Run();

public partial class Program { }
