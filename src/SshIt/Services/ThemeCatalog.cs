using System.Windows;
using System.Windows.Media;

namespace SshIt.Services;

/// <summary>
/// The selectable application themes that are not shipped as XAML.
///
/// Dark and Light stay as Themes/*.xaml: they are the boot dictionary in App.xaml and the
/// design-time preview. Every theme added after them is a palette here, because a theme is a
/// data change (ten colours) and not a 50-line XAML file that has to be kept in step.
/// </summary>
public static class ThemeCatalog
{
    public sealed record Palette(
        string Id,
        string Label,
        bool IsDark,
        string Window,
        string Panel,
        string Text,
        string Accent,
        string Success,
        string Warn,
        string Danger,
        string Info);

    public static IReadOnlyList<Palette> Palettes { get; } = new[]
    {
        // Cool blue-grey, dusty blue accent. Quiet enough to leave open all day.
        new Palette("Slate", "Slate", true,
            "#131820", "#171E27", "#E4E9EF", "#7FA6C8",
            "#6FBF8F", "#D9B26A", "#E0807E", "#8FB3D9"),

        // Neutral charcoal with a brass accent: no hue anywhere else.
        new Palette("Graphite", "Graphite", true,
            "#151516", "#1A1A1C", "#EAEAEA", "#C2A36B",
            "#8FBF8F", "#D6B36A", "#DE8079", "#9FB0C4"),

        // Deep teal-navy, glacier teal accent. Teal stays the owner's colour, just colder.
        new Palette("Fjord", "Fjord", true,
            "#0F1A1E", "#142126", "#E2EAEC", "#6FB3B8",
            "#7BC08F", "#D8B36A", "#E0817D", "#8FB4D9"),

        // Warm paper for daylight: brown ink, no blue cast in the surfaces.
        new Palette("Parchment", "Parchment", false,
            "#F5F0E6", "#EFE7D9", "#2A251F", "#8C6239",
            "#4F7A4A", "#A8701E", "#A6413C", "#4A6E93"),

        // Near-black navy with a periwinkle accent, for late sessions.
        new Palette("Ink", "Ink", true,
            "#0B0E17", "#10141F", "#E6E8F2", "#8C9BE0",
            "#6FBF9A", "#D6B36A", "#E0808F", "#93A7E8"),
    };

    public static Palette? Find(string? id)
        => Palettes.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Everything the theme switcher lists, in menu order.</summary>
    public static IReadOnlyList<(string Id, string Label)> MenuEntries { get; } =
        new (string Id, string Label)[] { ("System", "Use Windows setting"), ("Dark", "Dark"), ("Light", "Light") }
            .Concat(Palettes.Select(p => (p.Id, p.Label)))
            .Append(("HighContrast", "High contrast"))
            .ToArray();

    public static bool IsDark(string id) => Find(id)?.IsDark ?? true;

    /// <summary>Builds the brush dictionary for a palette: 34 keys plus the three colours.</summary>
    public static ResourceDictionary Build(Palette p)
    {
        var window = FromHex(p.Window);
        var panel = FromHex(p.Panel);
        var text = FromHex(p.Text);
        var accent = FromHex(p.Accent);

        // "Away" moves a surface further from the window in the direction this theme goes:
        // lighter on a dark theme, darker on a light one.
        Color Away(Color c, int steps)
        {
            var target = p.IsDark ? Colors.White : Colors.Black;
            if (steps < 0) target = p.IsDark ? Colors.Black : Colors.White;
            return Mix(c, target, Math.Abs(steps) * 0.012);
        }

        var panelAlt = Away(panel, 1);
        var elevated = Away(panel, 2);
        var control = Away(panel, 1);
        var controlHover = Away(panel, 3);
        var controlPressed = Away(panel, 4);
        var border = Mix(window, text, 0.14);
        var borderStrong = Mix(window, text, 0.24);
        var textMuted = Mix(window, text, 0.62);
        var textDisabled = Mix(window, text, 0.42);
        var accentHover = Away(accent, 2);
        var accentPressed = Away(accent, -2);
        var accentDim = Mix(window, accent, 0.18);

        // A label on the accent fill takes whichever of white and the window colour reads
        // better; muted accents sit in the middle where only one of the two works.
        var textOnAccent = ThemeService.ContrastRatio(Colors.White, accent)
                           >= ThemeService.ContrastRatio(window, accent)
            ? Colors.White
            : window;

        var dict = new ResourceDictionary();
        void Add(string key, Color colour)
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            dict[key] = brush;
        }

        Add("Brush.Window", window);
        Add("Brush.Panel", panel);
        Add("Brush.PanelAlt", panelAlt);
        Add("Brush.Elevated", elevated);
        Add("Brush.Control", control);
        Add("Brush.ControlHover", controlHover);
        Add("Brush.ControlPressed", controlPressed);
        Add("Brush.Border", border);
        Add("Brush.BorderStrong", borderStrong);
        Add("Brush.Text", text);
        Add("Brush.TextMuted", textMuted);
        Add("Brush.TextDisabled", textDisabled);
        Add("Brush.TextOnAccent", textOnAccent);
        Add("Brush.Accent", accent);
        Add("Brush.AccentHover", accentHover);
        Add("Brush.AccentPressed", accentPressed);
        Add("Brush.AccentDim", accentDim);
        Add("Brush.Success", FromHex(p.Success));
        Add("Brush.Warn", FromHex(p.Warn));
        Add("Brush.Danger", FromHex(p.Danger));
        Add("Brush.Info", FromHex(p.Info));
        Add("Brush.TerminalBackground", window);
        Add("Brush.TerminalForeground", text);
        Add("Brush.TerminalCursor", accent);
        Add("Brush.TerminalSelection", accentDim);
        Add("Brush.TerminalMatch", Mix(window, accent, 0.30));
        Add("Brush.TitleBar", window);
        Add("Brush.MenuBar", window);
        Add("Brush.ToolBar", window);
        Add("Brush.StatusBar", Away(window, -1));
        Add("Brush.TabActive", window);
        Add("Brush.TabInactive", panel);
        Add("Brush.TabHover", controlHover);
        Add("Brush.Selection", accentDim);
        Add("Brush.Hover", controlHover);

        dict["Color.Accent"] = accent;
        dict["Color.Window"] = window;
        dict["Color.Text"] = text;
        return dict;
    }

    private static Color FromHex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }
}
