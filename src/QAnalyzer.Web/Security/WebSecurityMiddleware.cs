using Microsoft.AspNetCore.Http;

namespace QAnalyzer.Web.Security;

/// <summary>
/// Menambahkan HTTP security headers defensif pada seluruh respons web dan API.
/// </summary>
public sealed class WebSecurityHeadersMiddleware(RequestDelegate next)
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; connect-src 'self'; object-src 'none'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers["Content-Security-Policy"] = ContentSecurityPolicy;

        if (WebApiExceptionMiddleware.IsApiPath(context.Request.Path))
        {
            headers["Cache-Control"] = "no-store";
            headers["Pragma"] = "no-cache";
        }

        return next(context);
    }
}

/// <summary>
/// Menormalkan error HTTP (JSON tidak valid, Content-Type salah, payload terlalu besar, dan exception tak terduga)
/// menjadi respons JSON yang konsisten tanpa membocorkan detail internal server.
/// </summary>
public sealed class WebApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);

            if (!context.Response.HasStarted
                && string.IsNullOrEmpty(context.Response.ContentType)
                && IsApiPath(context.Request.Path)
                && TryGetStandardClientErrorMessage(context.Response.StatusCode, out var clientError))
            {
                await context.Response.WriteAsJsonAsync(new { error = clientError });
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (BadHttpRequestException ex)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            var message = TryGetStandardClientErrorMessage(ex.StatusCode, out var mapped)
                ? mapped
                : "Permintaan tidak valid.";

            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = message });
        }
        catch (Exception)
        {
            if (context.Response.HasStarted)
            {
                throw;
            }

            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new { error = "Terjadi kesalahan internal pada server." });
        }
    }

    internal static bool IsApiPath(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/analyzer/api", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetStandardClientErrorMessage(int statusCode, out string message)
    {
        message = statusCode switch
        {
            StatusCodes.Status400BadRequest => "Format payload JSON tidak valid.",
            StatusCodes.Status405MethodNotAllowed => "Metode HTTP tidak diizinkan untuk endpoint ini.",
            StatusCodes.Status413PayloadTooLarge => "Ukuran payload melebihi batas maksimal server.",
            StatusCodes.Status415UnsupportedMediaType => "Content-Type harus application/json.",
            _ => string.Empty,
        };

        return message.Length > 0;
    }
}
