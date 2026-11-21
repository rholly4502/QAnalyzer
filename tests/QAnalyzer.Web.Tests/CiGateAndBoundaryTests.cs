using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace QAnalyzer.Web.Tests;

public class CiGateAndBoundaryTests
{
    private static readonly string[] ForbiddenSqlTokens =
    [
        "Microsoft.Data.SqlClient",
        "SqlConnection",
        "SqlDmvReader",
        "GetConnectionString",
        "ConnectionStrings",
    ];

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "QAnalyzer.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Tidak dapat menemukan root repository (QAnalyzer.slnx).");
    }

    [Fact]
    public void QAnalyzerWeb_Project_DoesNotReferenceSqlClient()
    {
        var csprojPath = Path.Combine(FindRepoRoot(), "src", "QAnalyzer.Web", "QAnalyzer.Web.csproj");
        Assert.True(File.Exists(csprojPath), $"File tidak ditemukan: {csprojPath}");

        var doc = XDocument.Load(csprojPath);
        var packageReferences = doc
            .Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include));

        Assert.DoesNotContain(
            packageReferences,
            include => string.Equals(include, "Microsoft.Data.SqlClient", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QAnalyzerWeb_SourceFiles_DoNotUseSqlClientOrConnectionStrings()
    {
        var webRoot = Path.Combine(FindRepoRoot(), "src", "QAnalyzer.Web");
        var sourceFiles = Directory
            .EnumerateFiles(webRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                var relative = Path.GetRelativePath(webRoot, path);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return !segments.Any(s =>
                    string.Equals(s, "bin", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s, "obj", StringComparison.OrdinalIgnoreCase));
            })
            .ToList();

        Assert.NotEmpty(sourceFiles);

        foreach (var file in sourceFiles)
        {
            var content = File.ReadAllText(file);
            foreach (var token in ForbiddenSqlTokens)
            {
                Assert.DoesNotContain(token, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void CiWorkflow_DefinesDotnetTestAndWebBuildGate()
    {
        var ciPath = Path.Combine(FindRepoRoot(), ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(ciPath), $"Workflow CI belum ada: {ciPath}");

        var yaml = File.ReadAllText(ciPath);

        Assert.Contains("pull_request:", yaml);
        Assert.Contains("workflow_call:", yaml);
        Assert.Contains("dotnet build", yaml);
        Assert.Contains("dotnet test", yaml);
        Assert.Contains("npm ci", yaml);
        Assert.Contains("npm run build", yaml);
        Assert.Contains("web", yaml);
    }

    [Fact]
    public void DeployWorkflow_RequiresCiGateBeforeDeploy()
    {
        var deployPath = Path.Combine(FindRepoRoot(), ".github", "workflows", "deploy.yml");
        Assert.True(File.Exists(deployPath), $"Workflow deploy tidak ditemukan: {deployPath}");

        var yaml = File.ReadAllText(deployPath);

        Assert.Contains("./.github/workflows/ci.yml", yaml);
        Assert.Matches(new Regex(@"(?m)^\s*needs:\s*(\[\s*ci\s*\]|ci)\s*$"), yaml);
    }

    [Fact]
    public void Dockerfile_UsesConsistentMultiStagePathsForWebAndDotnet()
    {
        var dockerfilePath = Path.Combine(FindRepoRoot(), "Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile tidak ditemukan: {dockerfilePath}");

        var content = File.ReadAllText(dockerfilePath);

        // Stage web harus bekerja di /build/web agar vite outDir ../src/QAnalyzer.Web/wwwroot -> /build/src/QAnalyzer.Web/wwwroot
        Assert.Matches(new Regex(@"(?m)^\s*WORKDIR\s+/build/web\s*$"), content);

        // Stage build berada di WORKDIR /src; COPY src/ src/ salah karena membuat /src/src/...
        Assert.DoesNotMatch(new Regex(@"(?m)^\s*COPY\s+src/\s+src/\s*$"), content);
        Assert.Contains("COPY src/QAnalyzer.Core/ QAnalyzer.Core/", content);
        Assert.Contains("COPY src/QAnalyzer.Web/ QAnalyzer.Web/", content);

        // Salinan wwwroot dari stage web harus masuk ke /src/QAnalyzer.Web/wwwroot, bukan ./src/QAnalyzer.Web/wwwroot
        Assert.DoesNotMatch(new Regex(@"(?m)(?:^|\s)\./src/QAnalyzer\.Web/wwwroot(?:\s|$)"), content);
        Assert.Contains("COPY --from=web /build/src/QAnalyzer.Web/wwwroot /src/QAnalyzer.Web/wwwroot", content);
    }

    [Fact]
    public void CiWorkflow_DefinesDockerBuildAndSmokeTestGate()
    {
        var ciPath = Path.Combine(FindRepoRoot(), ".github", "workflows", "ci.yml");
        Assert.True(File.Exists(ciPath), $"Workflow CI belum ada: {ciPath}");

        var yaml = File.ReadAllText(ciPath);

        Assert.Contains("docker build", yaml);
        Assert.Contains("/api/health", yaml);
        Assert.Contains("/api/analyze", yaml);
        Assert.Contains("/analyzer/", yaml);
    }
}
