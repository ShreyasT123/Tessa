namespace Tessellation.Core;

public static class LayoutEngine
{
    private const int MinimumTileSize = 32;

    public static IReadOnlyList<RectI> Calculate(
        WindowLayout layout,
        RectI area,
        int count,
        int outerGap,
        int innerGap,
        int scrollIndex = 0,
        double masterRatio = 0.55,
        double scrollColumnWidth = 0.5)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0 || !area.IsValid)
        {
            return [];
        }

        outerGap = Math.Max(0, outerGap);
        innerGap = Math.Max(0, innerGap);

        return layout switch
        {
            WindowLayout.Dwindle => Dwindle(area, count, outerGap, innerGap),
            WindowLayout.Master => Master(area, count, outerGap, innerGap, masterRatio),
            WindowLayout.Columns => GridCells(area, count, count, outerGap, innerGap),
            WindowLayout.Grid => Grid(area, count, outerGap, innerGap),
            WindowLayout.Monocle => Monocle(area, count, outerGap),
            WindowLayout.Scrolling => Scrolling(area, count, outerGap, innerGap, scrollIndex, scrollColumnWidth),
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
    }

    private static IReadOnlyList<RectI> Dwindle(RectI area, int count, int outer, int inner)
    {
        var current = Inset(area, outer);
        if (!current.IsValid)
        {
            return [];
        }

        var result = new List<RectI>(count);
        for (var index = 0; index < count; index++)
        {
            if (index == count - 1 || Math.Max(current.Width, current.Height) < MinimumTileSize * 2)
            {
                for (var remaining = index; remaining < count; remaining++)
                {
                    result.Add(current);
                }

                break;
            }

            if (current.Width >= current.Height)
            {
                var gap = FittedGap(current.Width, 2, inner);
                var firstWidth = Math.Max(1, (current.Width - gap) / 2);
                result.Add(new RectI(current.Left, current.Top, current.Left + firstWidth, current.Bottom));
                current = current with { Left = current.Left + firstWidth + gap };
            }
            else
            {
                var gap = FittedGap(current.Height, 2, inner);
                var firstHeight = Math.Max(1, (current.Height - gap) / 2);
                result.Add(new RectI(current.Left, current.Top, current.Right, current.Top + firstHeight));
                current = current with { Top = current.Top + firstHeight + gap };
            }

            if (!current.IsValid)
            {
                while (result.Count < count)
                {
                    result.Add(result[^1]);
                }

                break;
            }
        }

        return result;
    }

    private static IReadOnlyList<RectI> Master(RectI area, int count, int outer, int inner, double masterRatio)
    {
        var inset = Inset(area, outer);
        if (!inset.IsValid)
        {
            return [];
        }

        if (inset.Height < count - 1)
        {
            return GridCells(area, count, 1, outer, inner);
        }

        if (count == 1)
        {
            return [inset];
        }

        var columnGap = FittedGap(inset.Width, 2, inner);
        var availableWidth = inset.Width - columnGap;
        if (availableWidth < 2)
        {
            return GridCells(area, count, 1, outer, inner);
        }

        if (!double.IsFinite(masterRatio) || masterRatio is < 0.25 or > 0.75)
        {
            throw new ArgumentOutOfRangeException(nameof(masterRatio));
        }

        var masterWidth = Math.Clamp((int)Math.Round(availableWidth * masterRatio), 1, availableWidth - 1);
        var stackWidth = inset.Width - columnGap - masterWidth;
        if (stackWidth < 1)
        {
            return GridCells(area, count, 1, outer, inner);
        }

        var rowGap = FittedGap(inset.Height, count - 1, inner);
        var rowHeight = Math.Max(1, (inset.Height - rowGap * (count - 2)) / (count - 1));
        var result = new List<RectI>(count)
        {
            new(inset.Left, inset.Top, inset.Left + masterWidth, inset.Bottom)
        };

        for (var index = 0; index < count - 1; index++)
        {
            var top = inset.Top + index * (rowHeight + rowGap);
            var bottom = index == count - 2 ? inset.Bottom : top + rowHeight;
            result.Add(new RectI(inset.Left + masterWidth + columnGap, top, inset.Right, bottom));
        }

        return result;
    }

    private static IReadOnlyList<RectI> Grid(RectI area, int count, int outer, int inner)
    {
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        return GridCells(area, count, columns, outer, inner);
    }

    private static IReadOnlyList<RectI> GridCells(RectI area, int count, int columns, int outer, int inner)
    {
        var inset = Inset(area, outer);
        if (!inset.IsValid)
        {
            return [];
        }

        var rows = (count + columns - 1) / columns;
        if (inset.Width < columns || inset.Height < rows)
        {
            return Enumerable.Repeat(inset, count).ToArray();
        }

        var rowGap = FittedGap(inset.Height, rows, inner);
        var rowHeight = Math.Max(1, (inset.Height - rowGap * (rows - 1)) / rows);
        var result = new List<RectI>(count);

        for (var row = 0; row < rows; row++)
        {
            var start = row * columns;
            var cells = Math.Min(columns, count - start);
            var columnGap = FittedGap(inset.Width, cells, inner);
            var cellWidth = Math.Max(1, (inset.Width - columnGap * (cells - 1)) / cells);
            var top = inset.Top + row * (rowHeight + rowGap);
            var bottom = row == rows - 1 ? inset.Bottom : top + rowHeight;

            for (var column = 0; column < cells; column++)
            {
                var left = inset.Left + column * (cellWidth + columnGap);
                var right = column == cells - 1 ? inset.Right : left + cellWidth;
                result.Add(new RectI(left, top, right, bottom));
            }
        }

        return result;
    }

    private static IReadOnlyList<RectI> Monocle(RectI area, int count, int outer)
    {
        var tile = Inset(area, outer);
        return tile.IsValid ? Enumerable.Repeat(tile, count).ToArray() : [];
    }

    private static IReadOnlyList<RectI> Scrolling(
        RectI area,
        int count,
        int outer,
        int inner,
        int scrollIndex,
        double scrollColumnWidth)
    {
        var inset = Inset(area, outer);
        if (!inset.IsValid)
        {
            return [];
        }

        if (count == 1)
        {
            return [inset];
        }

        if (!double.IsFinite(scrollColumnWidth) || scrollColumnWidth is < 0.1 or > 1.0)
        {
            throw new ArgumentOutOfRangeException(nameof(scrollColumnWidth));
        }

        var columnWidth = Math.Clamp((int)Math.Round(inset.Width * scrollColumnWidth), 1, inset.Width);
        var step = columnWidth + inner;
        var offset = Math.Clamp(scrollIndex, 0, count - 1) * step;
        return Enumerable.Range(0, count)
            .Select(index =>
            {
                var left = inset.Left + index * step - offset;
                return new RectI(left, inset.Top, left + columnWidth, inset.Bottom);
            })
            .ToArray();
    }

    private static RectI Inset(RectI area, int gap) =>
        new(area.Left + gap, area.Top + gap, area.Right - gap, area.Bottom - gap);

    private static int FittedGap(int extent, int cells, int requested)
    {
        if (cells <= 1)
        {
            return 0;
        }

        return Math.Min(requested, Math.Max(0, (extent - cells) / (cells - 1)));
    }
}
