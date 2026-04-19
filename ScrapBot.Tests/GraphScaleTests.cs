using ScrapBot.Steam;

namespace ScrapBot.Tests;

public class GraphScaleTests
{
    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(1.5, 1)]
    [InlineData(3.1, 1)]
    [InlineData(9.9, 2)]
    [InlineData(20.0, 5)]
    [InlineData(61.0, 20)]
    public void GetNiceTickStep_ReturnsRoundedStep(double range, double expected)
    {
        Assert.Equal(expected, GraphScale.GetNiceTickStep(range));
    }

    [Fact]
    public void GetYAxisScale_PadsFlatSeries()
    {
        var scale = GraphScale.GetYAxisScale(5, 5);

        Assert.Equal(3, scale.Min);
        Assert.Equal(7, scale.Max);
        Assert.Equal(1, scale.Step);
    }

    [Fact]
    public void GetYAxisScale_PadsVariableSeriesWithoutNegativeMinimum()
    {
        var scale = GraphScale.GetYAxisScale(2, 10);

        Assert.Equal(0, scale.Min);
        Assert.Equal(12, scale.Max);
        Assert.Equal(2, scale.Step);
    }
}
