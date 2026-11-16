using Tessellation.Core;

namespace Tessellation.Tests;

public sealed class LayoutEngineTests
{
    public static IEnumerable<object[]> Layouts =>
        Enum.GetValues<WindowLayout>().Select(layout => new object[] { layout });

    [Theory]
    [MemberData(nameof(Layouts))]
    public void LayoutsReturnValidTilesForNarrowWorkAreas(WindowLayout layout)
    {
        var area = new RectI(0, 0, 2, 2);

        var tiles = LayoutEngine.Calculate(layout, area, 5, outerGap: 0, innerGap: 8);

        Assert.Equal(5, tiles.Count);
        Assert.All(tiles, tile => Assert.True(tile.IsValid));
    }

    [Fact]
    public void GridFallsBackToOverlappingValidTilesWhenTheAreaCannotFitCells()
    {
        var area = new RectI(-1, -1, 1, 1);

        var tiles = LayoutEngine.Calculate(WindowLayout.Grid, area, 5, outerGap: 0, innerGap: 0);

        Assert.Equal(5, tiles.Count);
        Assert.All(tiles, tile => Assert.Equal(area, tile));
    }

    [Fact]
    public void MasterFallsBackWhenItsStackCannotFitVertically()
    {
        var area = new RectI(0, 0, 100, 2);

        var tiles = LayoutEngine.Calculate(WindowLayout.Master, area, 4, outerGap: 0, innerGap: 0);

        Assert.Equal(4, tiles.Count);
        Assert.All(tiles, tile => Assert.True(tile.IsValid));
    }

    [Fact]
    public void ScrollingLayoutMovesTheVisibleColumnByOneColumnStep()
    {
        var area = new RectI(100, 50, 1100, 650);
        var unscrolled = LayoutEngine.Calculate(
            WindowLayout.Scrolling, area, 4, outerGap: 0, innerGap: 20, scrollColumnWidth: 0.5);
        var scrolled = LayoutEngine.Calculate(
            WindowLayout.Scrolling, area, 4, outerGap: 0, innerGap: 20, scrollIndex: 1, scrollColumnWidth: 0.5);

        Assert.Equal(-420, scrolled[0].Left);
        Assert.Equal(unscrolled[0].Left, scrolled[0].Left + 520);
        Assert.Equal(area.Left, scrolled[1].Left);
    }

    [Fact]
    public void InvalidWorkAreaProducesNoTiles()
    {
        var tiles = LayoutEngine.Calculate(
            WindowLayout.Grid, new RectI(10, 10, 10, 100), 3, outerGap: 0, innerGap: 0);

        Assert.Empty(tiles);
    }

    [Fact]
    public void ZeroWindowsProducesNoTiles()
    {
        foreach (var layout in Enum.GetValues<WindowLayout>())
        {
            var tiles = LayoutEngine.Calculate(layout, new RectI(0, 0, 100, 100), 0, 0, 0);

            Assert.Empty(tiles);
        }
    }

    [Fact]
    public void PositiveWorkAreasAlwaysProduceValidRectanglesAcrossSmallDimensions()
    {
        foreach (var layout in Enum.GetValues<WindowLayout>())
        {
            for (var width = 1; width <= 8; width++)
            {
                for (var height = 1; height <= 8; height++)
                {
                    for (var count = 1; count <= 8; count++)
                    {
                        var area = new RectI(-17, -11, -17 + width, -11 + height);
                        var tiles = LayoutEngine.Calculate(layout, area, count, 0, 4);

                        Assert.Equal(count, tiles.Count);
                        Assert.All(tiles, tile => Assert.True(tile.IsValid));
                    }
                }
            }
        }
    }

    [Fact]
    public void NegativeWindowCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LayoutEngine.Calculate(WindowLayout.Grid, new RectI(0, 0, 100, 100), -1, 0, 0));
    }
}
