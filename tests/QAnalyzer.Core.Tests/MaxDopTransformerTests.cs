using QAnalyzer.Core;

public sealed class MaxDopTransformerTests
{
    [Fact]
    public void AddMaxDopOne_AppendsToSimpleSelect()
    {
        var outSql = MaxDopTransformer.AddMaxDopOne("SELECT a FROM t;");
        Assert.EndsWith("OPTION (MAXDOP 1);", outSql);
    }

    [Fact]
    public void AddMaxDopOne_DoesNotTouchExistingOption()
    {
        var original = "SELECT a FROM t OPTION (RECOMPILE);";
        var outSql = MaxDopTransformer.AddMaxDopOne(original);
        Assert.Equal(original, outSql);
    }

    [Fact]
    public void AddMaxDopOne_Empty_ReturnsEmpty()
    {
        Assert.Equal("", MaxDopTransformer.AddMaxDopOne(""));
        Assert.Equal("   ", MaxDopTransformer.AddMaxDopOne("   "));
    }
}
