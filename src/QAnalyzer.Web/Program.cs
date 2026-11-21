using System.Globalization;
using System.Net.Http;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using QAnalyzer.Web.Security;
using QAnalyzer.Web.Services;

var app = Program.BuildApp(Program.CreateAppBuilder(args));
app.Run();

public partial class Program
{
    public static WebApplicationBuilder CreateAppBuilder(
        string[]? args = null,
        string? environmentName = null,
        string? contentRootPath = null,
        IDictionary<string, string?>? configurationOverrides = null)
    {
        var options = new WebApplicationOptions
        {
            Args = args ?? [],
            ApplicationName = typeof(Program).Assembly.GetName().Name,
            EnvironmentName = environmentName,
            ContentRootPath = contentRootPath,
        };

        var builder = WebApplication.CreateBuilder(options);
        if (configurationOverrides is not null)
        {
            builder.Configuration.AddInMemoryCollection(configurationOverrides);
        }

        ConfigureServices(builder);
        return builder;
    }

    public static void ConfigureServices(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var securityOptions = WebSecurityOptions.FromConfiguration(builder.Configuration);
        var trustedProxies = WebSecurityOptions.ResolveTrustedProxies(builder.Configuration);
        var tuningOptions = TuningGatewayOptions.FromConfiguration(builder.Configuration);
        var allowedOrigins = WebSecurityOptions.ResolveAllowedOrigins(
            builder.Configuration,
            builder.Environment.IsDevelopment());

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = securityOptions.MaxRequestBodyBytes;
        });

        builder.Services.Configure<RouteHandlerOptions>(options =>
        {
            options.ThrowOnBadRequest = true;
        });

        builder.Services.AddSingleton(securityOptions);
        builder.Services.AddSingleton(trustedProxies);
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
                var retryAfterSeconds = securityOptions.RateLimitWindowSeconds;
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                }

                context.HttpContext.Response.Headers.RetryAfter =
                    retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new { error = "Terlalu banyak permintaan. Coba lagi beberapa saat." },
                    cancellationToken);
            };

            options.AddPolicy("analyze", httpContext =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: WebSecurityOptions.ResolveRateLimitPartitionKey(httpContext.Connection.RemoteIpAddress),
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
                    partitionKey: WebSecurityOptions.ResolveRateLimitPartitionKey(httpContext.Connection.RemoteIpAddress),
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = tunePermitLimit,
                        Window = TimeSpan.FromSeconds(securityOptions.RateLimitWindowSeconds),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = securityOptions.RateLimitQueueLimit,
                    }));
        });
    }

    public static WebApplication BuildApp(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var app = builder.Build();
        ConfigurePipeline(app);
        return app;
    }

    public static void ConfigurePipeline(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var trustedProxies = app.Services.GetRequiredService<TrustedProxyOptions>();
        if (trustedProxies.HasEntries)
        {
            var forwardedOptions = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
                ForwardLimit = 1,
            };
            forwardedOptions.KnownProxies.Clear();
            forwardedOptions.KnownIPNetworks.Clear();
            foreach (var proxy in trustedProxies.Proxies)
            {
                forwardedOptions.KnownProxies.Add(proxy);
            }

            foreach (var network in trustedProxies.Networks)
            {
                forwardedOptions.KnownIPNetworks.Add(network);
            }

            app.UseForwardedHeaders(forwardedOptions);
        }

        app.UseMiddleware<WebSecurityHeadersMiddleware>();
        app.UseCors("QAnalyzerCors");
        app.UseMiddleware<WebApiExceptionMiddleware>();
        app.UseRateLimiter();

        var wwwrootIndexPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot", "index.html");
        var hasWwwroot = Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot"));

        app.Use(async (context, next) =>
        {
            await next(context);

            if (context.Response.HasStarted
                || context.Response.StatusCode != StatusCodes.Status404NotFound
                || context.GetEndpoint() is not null)
            {
                return;
            }

            if (WebApiExceptionMiddleware.IsApiPath(context.Request.Path))
            {
                await context.Response.WriteAsJsonAsync(
                    new { error = "Endpoint API tidak ditemukan." });
                return;
            }

            if (hasWwwroot
                && File.Exists(wwwrootIndexPath)
                && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(wwwrootIndexPath, context.RequestAborted);
            }
        });

        // Sajikan frontend hasil build (Vite) dari wwwroot bila ada.
        if (hasWwwroot)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions { RequestPath = "/analyzer" });
        }

        MapApiEndpoints(app.MapGroup("/api"));
        MapApiEndpoints(app.MapGroup("/analyzer/api"));
    }

    private static void MapApiEndpoints(RouteGroupBuilder group)
    {
        group.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        group.MapPost("/analyze", (AnalyzeRequest req, AnalyzerService svc, WebSecurityOptions opts) =>
        {
            var failure = WebSecurityValidator.ValidateAnalyzeRequest(req, opts.MaxSqlLength, opts.MaxPlanLength);
            if (failure is not null)
            {
                return Results.Json(new { error = failure.Error }, statusCode: failure.StatusCode);
            }

            try
            {
                var resp = svc.Analyze(req);
                return Results.Ok(resp);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireRateLimiting("analyze");

        group.MapPost("/tune", async (
            TuneRequest req,
            TuningService svc,
            WebSecurityOptions opts,
            TuningGatewayOptions tuningOpts,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var failure = WebSecurityValidator.ValidateTuneRequest(req, opts.MaxSqlLength, opts.MaxPlanLength);
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
                    var safeLogReason = (resp.Error ?? "unknown")
                        .Replace('\r', ' ')
                        .Replace('\n', ' ');
                    logger.LogWarning("AI tuning gateway request failed: {Reason}", safeLogReason);
                    return Results.Json(
                        new { error = "Gagal menghubungi layanan AI tuning. Coba lagi beberapa saat." },
                        statusCode: StatusCodes.Status502BadGateway);
                }

                return Results.Ok(resp);
            }
            catch (FormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        }).RequireRateLimiting("tune");
    }
}
