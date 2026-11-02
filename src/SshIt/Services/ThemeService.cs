using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SshIt.Services;

/// <summary>
/// Swaps the brush dictionary at runtime. Control styles bind with DynamicResource.
///
/// Windows high contrast wins over the requested dark/light theme. Its dictionary is built
/// from SystemColors rather than a XAML file, so it follows whichever high-contrast scheme
/// the user picked.
/// </summary>
public sealed class ThemeService
{
    /// <summary>Debug/test hook: forces high contrast without touching the OS setting.</summary>
    public static bool ForceHighContrast { get; set; }

    /// <summary>What the terminal theme asks: is the high-contrast dictionary live?</summary>
    public static bool HighContrastActive { get; private set; }

    public bool IsDark { get; private set; } = true;
    public bool HighContrast { get; private set; }

    /// <summary>Dark, Light, or one of the catalog palette ids — what is actually painted.</summary>
    public string CurrentTheme { get; private set; } = "Dark";

    public event Action? Changed;

    public void Apply(string mode)
    {
        var requested = string.IsNullOrWhiteSpace(mode) ? "System" : mode;

        HighContrast = ForceHighContrast
                       || string.Equals(requested, "HighContrast", StringComparison.OrdinalIgnoreCase)
                       || SystemParameters.HighContrast;

        var palette = ThemeCatalog.Find(requested);
        var resolved = palette?.Id;
        if (resolved is null)
        {
            resolved = string.Equals(requested, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark"
                : string.Equals(requested, "Light", StringComparison.OrdinalIgnoreCase) ? "Light"
                : SystemPrefersDark() ? "Dark" : "Light";
        }

        CurrentTheme = resolved;
        IsDark = HighContrast
            ? ContrastRatio(SystemColors.WindowColor, Colors.White) >= 4.5
            : palette?.IsDark ?? resolved == "Dark";

        HighContrastActive = HighContrast;
        ApplyCurrent();
    }

    public void Toggle()
    {
        IsDark = !IsDark;
        ApplyCurrent();
    }

    private void ApplyCurrent()
    {
        var app = Application.Current;
        if (app is null) return;

        ResourceDictionary dict;
        if (HighContrast) dict = BuildHighContrast();
        else if (ThemeCatalog.Find(CurrentTheme) is { } palette) dict = ThemeCatalog.Build(palette);
        else dict = new ResourceDictionary
        {
            Source = new Uri(IsDark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative)
        };

        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
            merged[0] = dict;
        else
            merged.Add(dict);

        app.Resources["IsDarkTheme"] = IsDark;
        app.Resources["IsHighContrast"] = HighContrast;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- high contrast

    /// <summary>Every brush key the chrome binds, so a missing one cannot leave a control unthemed.</summary>
    public static readonly string[] BrushKeys =
    {
        "Brush.Accent", "Brush.AccentDim", "Brush.AccentHover", "Brush.AccentPressed",
        "Brush.Border", "Brush.BorderStrong", "Brush.Control", "Brush.ControlHover",
        "Brush.ControlPressed", "Brush.Danger", "Brush.Elevated", "Brush.Hover",
        "Brush.Info", "Brush.MenuBar", "Brush.Panel", "Brush.PanelAlt", "Brush.Selection",
        "Brush.StatusBar", "Brush.Success", "Brush.TabActive", "Brush.TabHover",
        "Brush.TabInactive", "Brush.TerminalBackground", "Brush.TerminalCursor",
        "Brush.TerminalForeground", "Brush.TerminalMatch", "Brush.TerminalSelection",
        "Brush.Text", "Brush.TextDisabled", "Brush.TextMuted", "Brush.TextOnAccent",
        "Brush.TitleBar", "Brush.ToolBar", "Brush.Warn", "Brush.Window",
    };

    /// <summary>
    /// The chrome palette in high contrast: two colours and the current selection, taken from
    /// the user's scheme. Semantic colours (success/warn/danger) deliberately collapse to
    /// window text — high contrast must not carry meaning in hue alone.
    /// </summary>
    public static ResourceDictionary BuildHighContrast()
    {
        var window = SystemColors.WindowColor;
        var text = SystemColors.WindowTextColor;
        var highlight = SystemColors.HighlightColor;
        var highlightText = SystemColors.HighlightTextColor;
        var disabled = SystemColors.GrayTextColor;

        // The scheme's own highlight is used as a fill, an accent and a frame. Keep it when it
        // can be seen on the window (3:1, the non-text bar) AND carries a label at 4.5:1;
        // otherwise the safest pair is the window's own text colour with the window as label.
        var (accent, accentText) = AccentPair(window, text, highlight, highlightText);

        var dict = new ResourceDictionary();
        void Add(string key, Color colour) => dict[key] = Frozen(colour);

        Add("Brush.Window", window);
        Add("Brush.Panel", window);
        Add("Brush.PanelAlt", window);
        Add("Brush.Elevated", window);
        Add("Brush.Control", window);
        Add("Brush.ControlHover", accent);
        Add("Brush.ControlPressed", accent);
        Add("Brush.Border", text);
        Add("Brush.BorderStrong", text);
        Add("Brush.Text", text);
        Add("Brush.TextMuted", text);
        Add("Brush.TextDisabled", disabled);
        Add("Brush.TextOnAccent", accentText);
        Add("Brush.Accent", accent);
        Add("Brush.AccentHover", accent);
        Add("Brush.AccentPressed", accent);
        Add("Brush.AccentDim", window);
        Add("Brush.Success", text);
        Add("Brush.Warn", text);
        Add("Brush.Danger", text);
        Add("Brush.Info", text);
        Add("Brush.TerminalBackground", window);
        Add("Brush.TerminalForeground", text);
        Add("Brush.TerminalCursor", text);
        Add("Brush.TerminalSelection", accent);
        Add("Brush.TerminalMatch", accent);
        Add("Brush.TitleBar", window);
        Add("Brush.MenuBar", window);
        Add("Brush.ToolBar", window);
        Add("Brush.StatusBar", window);
        Add("Brush.TabActive", window);
        Add("Brush.TabInactive", window);
        Add("Brush.TabHover", accent);
        Add("Brush.Selection", accent);
        Add("Brush.Hover", accent);

        dict["Color.Accent"] = accent;
        dict["Color.Window"] = window;
        dict["Color.Text"] = text;
        return dict;
    }

    /// <summary>
    /// The accent fill and the label that goes inside it. Prefers the scheme's highlight, then
    /// falls back to the window's text colour so the pair is always readable.
    /// </summary>
    public static (Color Fill, Color Label) AccentPair(Color window, Color text, Color highlight, Color highlightText)
    {
        var label = ContrastRatio(highlightText, highlight) >= ContrastRatio(window, highlight)
            ? highlightText
            : window;

        if (ContrastRatio(label, highlight) >= 4.5 && ContrastRatio(highlight, window) >= 3.0)
            return (highlight, label);

        return (text, window);
    }

    /// <summary>WCAG contrast ratio: 1.0 (identical) to 21.0 (black on white).</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return true;
        }
    }
}
