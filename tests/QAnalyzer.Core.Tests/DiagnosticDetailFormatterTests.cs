using QAnalyzer.Core;

namespace QAnalyzer.Core.Tests;

public class DiagnosticDetailFormatterTests
{
    [Fact]
    public void Format_WithSuggestionAndPosition_IncludesAllParts()
    {
        var d = new Diagnostic(
            RiskLevel.Critical,
            "UpdateWithoutWhereRule",
            "Update tanpa WHERE",
            "UPDATE users SET active = 0",
            Suggestion: "Tambahkan klausa WHERE atau tabel log.",
            Line: 12,
            Column: 3);

        var text = DiagnosticDetailFormatter.Format(d);

        Assert.Contains("[Critical]", text);
        Assert.Contains("Update tanpa WHERE (UpdateWithoutWhereRule)", text);
        Assert.Contains("Baris 12, Kolom 3", text);
        Assert.Contains("UPDATE users SET active = 0", text);
        Assert.Contains("Saran: Tambahkan klausa WHERE atau tabel log.", text);
    }

    [Fact]
    public void Format_WithoutSuggestion_OmitsSaranLine()
    {
        var d = new Diagnostic(RiskLevel.Info, "NoLockRule", "NOLOCK", "WITH (NOLOCK) dipakai di SELECT polos");

        var text = DiagnosticDetailFormatter.Format(d);

        Assert.DoesNotContain("Saran:", text);
        Assert.Contains("NOLOCK", text);
    }

    [Fact]
    public void Format_ColumnZero_OmitsColumn()
    {
        var d = new Diagnostic(RiskLevel.Warning, "SelectStarRule", "SELECT *", "SELECT *", Line: 4);

        var text = DiagnosticDetailFormatter.Format(d);

        Assert.Contains("Baris 4", text);
        Assert.DoesNotContain("Kolom", text);
    }
}
