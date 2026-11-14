using QAnalyzer.Core;

namespace QAnalyzer.Core.Tests;

public class ReportFormatterTests
{
    private static Diagnostic D(RiskLevel level, string rule, string message, int line = 0, string? sug = null)
        => new(level, rule, "Title", message, sug, line, 0);

    [Fact]
    public void ToMarkdown_Empty_HasNoSections()
    {
        var md = ReportFormatter.ToMarkdown([]);

        Assert.Contains("# QAnalyzer Report", md);
        Assert.DoesNotContain("## ", md);
    }

    [Fact]
    public void ToMarkdown_GroupsBySeverityDescending()
    {
        var diags = new[]
        {
            D(RiskLevel.Info, "I", "im"),
            D(RiskLevel.Critical, "C", "cm"),
            D(RiskLevel.Warning, "W", "wm"),
        };

        var md = ReportFormatter.ToMarkdown(diags);

        var iC = md.IndexOf("## Critical", StringComparison.Ordinal);
        var iW = md.IndexOf("## Warning", StringComparison.Ordinal);
        var iI = md.IndexOf("## Info", StringComparison.Ordinal);
        Assert.True(iC >= 0 && iW > iC && iI > iW, "urutan level harus Critical > Warning > Info");
    }

    [Fact]
    public void ToMarkdown_IncludesDiagnosticDetails()
    {
        var d = D(RiskLevel.Critical, "UPDATE_NO_WHERE", "Update tanpa WHERE ditemukan.", 5, "Tambahkan WHERE.");

        var md = ReportFormatter.ToMarkdown(new[] { d });

        Assert.Contains("[Line 5]", md);
        Assert.Contains("UPDATE_NO_WHERE", md);
        Assert.Contains("Update tanpa WHERE ditemukan.", md);
        Assert.Contains("Tambahkan WHERE.", md);
    }

    [Fact]
    public void ToCsv_WritesHeaderRow()
    {
        var csv = ReportFormatter.ToCsv(new[] { D(RiskLevel.Warning, "R1", "msg") });

        var first = csv.Split('\n')[0].TrimEnd('\r');
        Assert.Equal("Level,RuleId,Line,Column,Message,Suggestion", first);
    }

    [Fact]
    public void ToCsv_OneRowPerDiagnostic_InOrder()
    {
        var diags = new[]
        {
            D(RiskLevel.Critical, "A", "m1", 1),
            D(RiskLevel.Info, "B", "m2", 2),
        };

        var csv = ReportFormatter.ToCsv(diags);

        var lines = csv.TrimEnd('\n', '\r').Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("Critical,A,1,0,m1,", lines[1]);
        Assert.Contains("Info,B,2,0,m2,", lines[2]);
    }

    [Fact]
    public void ToCsv_EscapesFieldsContainingCommaAndQuote()
    {
        var d = D(RiskLevel.Warning, "R", "he said \"hi\", ok");

        var csv = ReportFormatter.ToCsv(new[] { d });

        Assert.Contains("\"he said \"\"hi\"\", ok\"", csv);
    }
}
