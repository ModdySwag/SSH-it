using System.Globalization;

namespace SshIt.Services;

/// <summary>Where one pane sits in a <see cref="TmuxLayout"/> window, in grid cells.</summary>
public sealed record TmuxGridCell(int PaneId, int Column, int ColumnSpan, int Row, int RowSpan,
    int Width, int Height);

/// <summary>
/// Turns tmux's layout tree into a WPF-friendly grid: star widths per column, star heights per row,
/// and one cell per pane. Pure so the mapping can be checked without a window.
///
/// The layout is a guillotine tree, so every pane's X is one of a handful of boundaries. Terminal
/// cells between two panes belong to the layout (`80x24{40x24,0,0,0,39x24,41,0,1}` is 40 + ONE
/// separator cell + 39): the boundary at x=41 makes the first column 41 cells wide, so the pane
/// borders tmux draws in that cell are part of the grid rather than a rounding gap.
/// </summary>
public sealed class TmuxGridMap
{
    public List<int> ColumnWidths { get; } = new();
    public List<int> RowHeights { get; } = new();
    public List<TmuxGridCell> Cells { get; } = new();

    public static TmuxGridMap Map(TmuxLayout root)
    {
        var leaves = root.Leaves().ToList();
        if (leaves.Count == 0) throw new FormatException("a layout with no panes");

        var xs = leaves.Select(l => l.X).Distinct().OrderBy(x => x).ToList();
        var ys = leaves.Select(l => l.Y).Distinct().OrderBy(y => y).ToList();

        var map = new TmuxGridMap();

        for (var i = 0; i < xs.Count; i++)
        {
            // The last column owns whatever the window has left, so rounding cannot leave a stripe.
            var next = i + 1 < xs.Count ? xs[i + 1] : Math.Max(root.Width, leaves.Max(l => l.X + l.Width));
            map.ColumnWidths.Add(Math.Max(1, next - xs[i]));
        }
        for (var i = 0; i < ys.Count; i++)
        {
            var next = i + 1 < ys.Count ? ys[i + 1] : Math.Max(root.Height, leaves.Max(l => l.Y + l.Height));
            map.RowHeights.Add(Math.Max(1, next - ys[i]));
        }

        foreach (var leaf in leaves)
        {
            var column = xs.IndexOf(leaf.X);
            var row = ys.IndexOf(leaf.Y);
            var columnSpan = 1;
            while (column + columnSpan < xs.Count && xs[column + columnSpan] < leaf.X + leaf.Width) columnSpan++;
            var rowSpan = 1;
            while (row + rowSpan < ys.Count && ys[row + rowSpan] < leaf.Y + leaf.Height) rowSpan++;

            map.Cells.Add(new TmuxGridCell(leaf.PaneId, column, columnSpan, row, rowSpan,
                leaf.Width, leaf.Height));
        }

        return map;
    }

    /// <summary>A short human summary, used by the probe and the suite.</summary>
    public string Describe() => string.Join(" ", Cells.Select(c =>
        string.Create(CultureInfo.InvariantCulture,
            $"%{c.PaneId}@({c.Column},{c.Row})+{c.ColumnSpan}x{c.RowSpan}")));
}
