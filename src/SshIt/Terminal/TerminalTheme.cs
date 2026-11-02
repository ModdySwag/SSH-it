using System.Windows;
using System.Windows.Media;
using SshIt.Services;

namespace SshIt.Terminal;

/// <summary>A terminal colour scheme: 16 ANSI colours, 256-colour cube plus chrome colours.</summary>
public sealed class TerminalTheme
{
    public string Name { get; init; } = "Campbell";
    public Color Background { get; init; }
    public Color Foreground { get; init; }
    public Color Cursor { get; init; }
    public Color Selection { get; init; }

    /// <summary>Search-hit colour. Unused when <see cref="OutlinedHighlights"/> is set.</summary>
    public Color Highlight { get; init; } = Color.FromRgb(0xF5, 0xC2, 0x42);

    /// <summary>
    /// Draw selection and search hits as frames rather than translucent fills: in high
    /// contrast a fill over glyphs is what makes text unreadable.
    /// </summary>
    public bool OutlinedHighlights { get; init; }

    public Color[] Palette { get; init; } = new Color[256];

    public Color Resolve(uint packed, bool foreground)
    {
        if (TermColor.IsDefault(packed)) return foreground ? Foreground : Background;
        if (TermColor.IsIndexed(packed)) return Palette[Math.Clamp(TermColor.Index(packed), 0, 255)];
        var (r, g, b) = TermColor.RgbParts(packed);
        return Color.FromRgb(r, g, b);
    }

    public static TerminalTheme Create(string name, Color background, Color foreground, Color cursor,
                                       Color selection, string[] ansi, Color? highlight = null,
                                       bool outlinedHighlights = false)
    {
        var palette = new Color[256];
        for (var i = 0; i < 16; i++)
            palette[i] = (Color)ColorConverter.ConvertFromString(ansi[i]);

        // 6x6x6 colour cube, then the 24-step grey ramp, per the xterm standard.
        var levels = new[] { 0, 95, 135, 175, 215, 255 };
        for (var i = 0; i < 216; i++)
        {
            var r = levels[i / 36];
            var g = levels[i / 6 % 6];
            var b = levels[i % 6];
            palette[16 + i] = Color.FromRgb((byte)r, (byte)g, (byte)b);
        }
        for (var i = 0; i < 24; i++)
        {
            var v = (byte)(8 + i * 10);
            palette[232 + i] = Color.FromRgb(v, v, v);
        }

        return new TerminalTheme
        {
            Name = name,
            Background = background,
            Foreground = foreground,
            Cursor = cursor,
            Selection = selection,
            Highlight = highlight ?? Color.FromRgb(0xF5, 0xC2, 0x42),
            OutlinedHighlights = outlinedHighlights,
            Palette = palette
        };
    }

    private static readonly string[] CampbellAnsi =
    {
        "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
        "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2"
    };

    private static readonly string[] SolarizedDarkAnsi =
    {
        "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
        "#002B36", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3"
    };

    private static readonly string[] NordAnsi =
    {
        "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
        "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4"
    };

    private static readonly string[] DraculaAnsi =
    {
        "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
        "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF"
    };

    private static readonly string[] OneHalfDarkAnsi =
    {
        "#282C34", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4",
        "#5A6374", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#DCDFE4"
    };

    private static readonly string[] TokyoNightAnsi =
    {
        "#15161E", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#A9B1D6",
        "#414868", "#F7768E", "#9ECE6A", "#E0AF68", "#7AA2F7", "#BB9AF7", "#7DCFFF", "#C0CAF5"
    };

    private static readonly string[] LightAnsi =
    {
        "#000000", "#C41A16", "#007400", "#826B28", "#0000FF", "#A90D91", "#0F7E8A", "#B2B2B2",
        "#666666", "#C41A16", "#007400", "#826B28", "#0000FF", "#A90D91", "#0F7E8A", "#000000"
    };

    // High-contrast ANSI, split by background luminance. Every entry from 1 to 15 clears
    // WCAG AA (4.5:1) against its own background; entry 0 is the background itself, which
    // is what "black" means on a black terminal.
    private static readonly string[] HighContrastOnDarkAnsi =
    {
        "#000000", "#FF6B6B", "#5CE65C", "#F2E85C", "#7DA9FF", "#FF86FF", "#4FE3E3", "#FFFFFF",
        "#A8A8A8", "#FF9B9B", "#96FF96", "#FFF59B", "#B6CCFF", "#FFB6FF", "#9BF5F5", "#FFFFFF"
    };

    private static readonly string[] HighContrastOnLightAnsi =
    {
        "#FFFFFF", "#B30000", "#0A6600", "#7A5B00", "#1B3FCC", "#8A1B8A", "#006B70", "#000000",
        "#5A5A5A", "#8A0000", "#005000", "#5C4400", "#12309A", "#6B146B", "#005258", "#000000"
    };

    public static IReadOnlyList<TerminalTheme> BuiltIn { get; } = new[]
    {
        Create("SSH-it Dark", Color.FromRgb(0x0B, 0x10, 0x13), Color.FromRgb(0xDC, 0xE7, 0xEA),
               Color.FromRgb(0x2F, 0xD4, 0xB6), Color.FromRgb(0x1F, 0x4A, 0x45), CampbellAnsi),
        Create("Campbell", Color.FromRgb(0x0C, 0x0C, 0x0C), Color.FromRgb(0xCC, 0xCC, 0xCC),
               Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x26, 0x4F, 0x78), CampbellAnsi),
        Create("Solarized Dark", Color.FromRgb(0x00, 0x2B, 0x36), Color.FromRgb(0x83, 0x94, 0x96),
               Color.FromRgb(0x93, 0xA1, 0xA1), Color.FromRgb(0x07, 0x36, 0x42), SolarizedDarkAnsi),
        Create("Nord", Color.FromRgb(0x2E, 0x34, 0x40), Color.FromRgb(0xD8, 0xDE, 0xE9),
               Color.FromRgb(0x88, 0xC0, 0xD0), Color.FromRgb(0x43, 0x4C, 0x5E), NordAnsi),
        Create("Dracula", Color.FromRgb(0x28, 0x2A, 0x36), Color.FromRgb(0xF8, 0xF8, 0xF2),
               Color.FromRgb(0xF8, 0xF8, 0xF2), Color.FromRgb(0x44, 0x47, 0x5A), DraculaAnsi),
        Create("One Half Dark", Color.FromRgb(0x28, 0x2C, 0x34), Color.FromRgb(0xDC, 0xDF, 0xE4),
               Color.FromRgb(0x5C, 0x63, 0x70), Color.FromRgb(0x3E, 0x44, 0x51), OneHalfDarkAnsi),
        Create("Tokyo Night", Color.FromRgb(0x1A, 0x1B, 0x26), Color.FromRgb(0xA9, 0xB1, 0xD6),
               Color.FromRgb(0xC0, 0xCA, 0xF5), Color.FromRgb(0x33, 0x3A, 0x4E), TokyoNightAnsi),
        Create("Light", Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x10, 0x1B, 0x1E),
               Color.FromRgb(0x0E, 0x9E, 0x86), Color.FromRgb(0xBF, 0xE8, 0xE1), LightAnsi),
    };

    /// <summary>
    /// The high-contrast scheme, built from the live system colours so it follows the user's
    /// own high-contrast palette (window, window text, selection). The ANSI ramp is chosen
    /// from the background's luminance, and selection/search are drawn as frames instead of
    /// translucent fills, because a fill over glyphs is what makes text unreadable.
    /// </summary>
    public static TerminalTheme HighContrast()
    {
        var background = SystemColors.WindowColor;
        var foreground = SystemColors.WindowTextColor;

        // Pick whichever 16-colour ramp is easier to read on this scheme's background; on an
        // unusual mid-tone scheme neither clears AA, so legibility wins and colour goes away.
        var onDark = MinimumContrast(HighContrastOnDarkAnsi, background);
        var onLight = MinimumContrast(HighContrastOnLightAnsi, background);
        var ansi = Math.Max(onDark, onLight) < 4.5
            ? MonoAnsi(foreground)
            : onLight >= onDark ? HighContrastOnLightAnsi : HighContrastOnDarkAnsi;

        var accent = ThemeService.ContrastRatio(SystemColors.HighlightColor, background) >= 4.5
            ? SystemColors.HighlightColor
            : foreground;

        return Create("High Contrast", background, foreground, foreground, accent,
                      ansi, accent, outlinedHighlights: true);
    }

    private static double MinimumContrast(string[] ansi, Color background)
    {
        var min = double.MaxValue;
        for (var i = 1; i < 16; i++)
            min = Math.Min(min, ThemeService.ContrastRatio((Color)ColorConverter.ConvertFromString(ansi[i]), background));
        return min;
    }

    private static string[] MonoAnsi(Color foreground)
    {
        var hex = "#" + foreground.R.ToString("X2") + foreground.G.ToString("X2") + foreground.B.ToString("X2");
        var ansi = new string[16];
        for (var i = 0; i < ansi.Length; i++) ansi[i] = hex;
        return ansi;
    }

    public static TerminalTheme Get(string? name)
    {
        if (ThemeService.HighContrastActive || ThemeService.ForceHighContrast) return HighContrast();
        if (string.Equals(name, "High Contrast", StringComparison.OrdinalIgnoreCase)) return HighContrast();
        return BuiltIn.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? BuiltIn[0];
    }
}
