using QAnalyzer.Core;

namespace QAnalyzer.Core.Tests;

public class DiagnosticFilterTests
{
    private static Diagnostic D(RiskLevel level) => new(level, "R", "T", "M");

    [Fact]
    public void CountByLevel_CountsEachSeveritySeparately()
    {
        var diags = new[] { D(RiskLevel.Critical), D(RiskLevel.Warning), D(RiskLevel.Info), D(RiskLevel.Critical) };

        var c = DiagnosticFilter.CountByLevel(diags);

        Assert.Equal(2, c.Critical);
        Assert.Equal(1, c.Warning);
        Assert.Equal(1, c.Info);
        Assert.Equal(4, c.Total);
    }

    [Fact]
    public void Apply_FiltersOutUncheckedLevels()
    {
        var diags = new[] { D(RiskLevel.Critical), D(RiskLevel.Warning), D(RiskLevel.Info) };
        var filter = new LevelFilter { Info = false, Warning = false, Critical = true };

        var result = DiagnosticFilter.Apply(diags, filter).ToList();

        Assert.Single(result);
        Assert.Equal(RiskLevel.Critical, result[0].Level);
    }

    [Fact]
    public void Apply_AllUnchecked_ReturnsEmpty()
    {
        var diags = new[] { D(RiskLevel.Critical) };
        var filter = new LevelFilter { Critical = false, Warning = false, Info = false };

        Assert.Empty(DiagnosticFilter.Apply(diags, filter));
    }

    [Fact]
    public void IsVisible_Defaults_AllVisible()
    {
        var f = new LevelFilter();

        Assert.True(f.IsVisible(RiskLevel.Info));
        Assert.True(f.IsVisible(RiskLevel.Warning));
        Assert.True(f.IsVisible(RiskLevel.Critical));
    }
}
