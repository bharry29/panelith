using Panelith.App.Controls;

namespace Panelith.App.Tests;

public sealed class FilmstripTests
{
    [Fact]
    public void MovingTheStartPastTheEndStopsOneSecondShort() =>
        Assert.Equal((9d, 10d), Filmstrip.Limit(12d, 10d, 100d, 60d, FilmstripEdge.Start));

    [Fact]
    public void MovingTheEndBeforeTheStartStopsOneSecondAfter() =>
        Assert.Equal((10d, 11d), Filmstrip.Limit(10d, 3d, 100d, 60d, FilmstripEdge.End));

    [Fact]
    public void APartCannotGrowPastTheLongestClip()
    {
        Assert.Equal((40d, 100d), Filmstrip.Limit(5d, 100d, 100d, 60d, FilmstripEdge.Start));
        Assert.Equal((10d, 70d), Filmstrip.Limit(10d, 95d, 100d, 60d, FilmstripEdge.End));
    }

    [Fact]
    public void MovingThePartKeepsItsLengthAndStaysInsideTheVideo()
    {
        Assert.Equal((70d, 100d), Filmstrip.Limit(85d, 115d, 100d, 60d, FilmstripEdge.Both));
        Assert.Equal((0d, 30d), Filmstrip.Limit(-5d, 25d, 100d, 60d, FilmstripEdge.Both));
    }

    [Fact]
    public void AVideoShorterThanASecondIsUsedWhole() =>
        Assert.Equal((0d, 0.6d), Filmstrip.Limit(0d, 0.2d, 0.6d, 60d, FilmstripEdge.End));
}
