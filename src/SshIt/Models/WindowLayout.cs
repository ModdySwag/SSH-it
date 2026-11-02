namespace SshIt.Models;

/// <summary>
/// Placement and tab list for one extra main window. The primary window stores the same
/// facts in the legacy AppSettings fields; this type covers every window after it.
/// </summary>
public sealed class WindowLayout
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }

    /// <summary>Session ids this window had open, in tab order.</summary>
    public List<string> TabSessionIds { get; set; } = new();
}
