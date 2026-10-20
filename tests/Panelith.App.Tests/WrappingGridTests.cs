using Panelith.App.Controls;

namespace Panelith.App;

public sealed class WrappingGridTests
{
    // The content area of the default 1320px window, beside the navigation rail.
    private const double NormalWindow = 1048;

    // The content area of this machine's 5120px monitor with the window maximised.
    private const double Ultrawide = 4888;

    [Fact]
    public void TheDefaultWindowKeepsThreeStatTilesAcross()
    {
        var (columns, width) = WrappingGrid.Plan(NormalWindow, count: 6, minItemWidth: 300, maxItemWidth: 420, spacing: 14);

        Assert.Equal(3, columns);
        Assert.InRange(width, 300, 420);
    }

    [Fact]
    public void AnUltrawidePutsEveryStatTileOnOneRowAtACappedWidth()
    {
        var (columns, width) = WrappingGrid.Plan(Ultrawide, count: 6, minItemWidth: 300, maxItemWidth: 420, spacing: 14);

        Assert.Equal(6, columns);
        Assert.Equal(420, width);
    }

    [Fact]
    public void TheDefaultWindowKeepsTwoScreenCardsAcross()
    {
        var (columns, width) = WrappingGrid.Plan(NormalWindow, count: 8, minItemWidth: 400, maxItemWidth: 520, spacing: 16);

        Assert.Equal(2, columns);
        Assert.InRange(width, 400, 520);
    }

    [Fact]
    public void AnUltrawideAddsScreenColumnsInsteadOfStretchingTwo()
    {
        var (columns, width) = WrappingGrid.Plan(Ultrawide, count: 8, minItemWidth: 400, maxItemWidth: 520, spacing: 16);

        Assert.Equal(8, columns);
        Assert.Equal(520, width);
    }

    [Fact]
    public void AVeryNarrowWindowShrinksToOneColumnRatherThanOverflowing()
    {
        var (columns, width) = WrappingGrid.Plan(280, count: 4, minItemWidth: 400, maxItemWidth: 520, spacing: 16);

        Assert.Equal(1, columns);
        Assert.Equal(280, width);
    }

    [Fact]
    public void NoChildrenDoesNotDivideByZero()
    {
        var (columns, width) = WrappingGrid.Plan(Ultrawide, count: 0, minItemWidth: 300, maxItemWidth: 420, spacing: 14);

        Assert.Equal(1, columns);
        Assert.Equal(0, width);
    }
}
