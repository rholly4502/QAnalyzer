using Microsoft.AspNetCore.StaticFiles;
using QAnalyzer.Web.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<AnalyzerService>();
builder.Services.AddCors(o =>
    o.AddPolicy("dev", p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors("dev");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/api/analyze", (AnalyzeRequest req, AnalyzerService svc) =>
{
    if (string.IsNullOrWhiteSpace(req.Sql))
        return Results.BadRequest(new { error = "Sql wajib diisi." });
    var resp = svc.Analyze(req);
    return Results.Ok(resp);
});

// Sajikan frontend hasil build (Vite) dari wwwroot bila ada.
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions { RequestPath = "/analyzer" });
    app.MapFallbackToFile("index.html");
}

app.Run();

public partial class Program { }
