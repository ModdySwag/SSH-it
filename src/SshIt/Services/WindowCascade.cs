using System.Windows;

namespace SshIt.Services;

/// <summary>
/// Where a newly opened second window goes. It steps down-right from the window it was
/// opened from and wraps back to the work area's top-left once it would leave the screen,
/// so a run of new windows never disappears off an edge.
/// </summary>
public static class WindowCascade
{
    public const double Step = 32;

    /// <summary>The smallest a window may be shrunk to in this calculation.</summary>
    public const double Minimum = 320;

    public static Rect Next(Rect origin, Rect workArea, double width, double height)
    {
        width = Math.Max(Minimum, Math.Min(width, workArea.Width));
        height = Math.Max(Minimum, Math.Min(height, workArea.Height));

        var x = origin.Left + Step;
        var y = origin.Top + Step;

        if (x + width > workArea.Right || x < workArea.Left) x = workArea.Left + Step;
        if (y + height > workArea.Bottom || y < workArea.Top) y = workArea.Top + Step;

        x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - width));
        y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - height));

        return new Rect(x, y, width, height);
    }
}
