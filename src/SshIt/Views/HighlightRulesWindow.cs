using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SshIt.Models;

namespace SshIt.Views;

/// <summary>
/// Editor for keyword-highlighting rules. Works on a copy of the list and only hands it back on
/// OK, so cancelling leaves the running settings untouched.
/// </summary>
public sealed class HighlightRulesWindow : Window
{
    private static readonly (string Name, string Hex)[] Backgrounds =
    {
        ("None", ""),
        ("Red", "#5A1F1F"),
        ("Amber", "#5A441F"),
        ("Green", "#1E4A2C"),
        ("Teal", "#174A4A"),
        ("Blue", "#1F3A5A"),
        ("Purple", "#3A2050"),
        ("Grey", "#333333")
    };

    private static readonly (string Name, string Hex)[] Foregrounds =
    {
        ("Keep", ""),
        ("Red", "#FF8A8A"),
        ("Amber", "#FFD479"),
        ("Green", "#8CFF9E"),
        ("Teal", "#6FE8E8"),
        ("Blue", "#8CCBFF"),
        ("Purple", "#DFA9FF"),
        ("White", "#FFFFFF"),
        ("Grey", "#B0B0B0")
    };

    private readonly List<HighlightRule> _rules;
    private readonly ListBox _list = new() { Height = 220 };
    private readonly TextBox _pattern = new() { Width = 300 };
    private readonly ComboBox _bg = new() { Width = 140 };
    private readonly ComboBox _fg = new() { Width = 140 };
    private readonly CheckBox _ignoreCase = new() { Content = "Ignore case", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _regex = new() { Content = "Regular expression", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _bold = new() { Content = "Bold", Margin = new Thickness(0, 4, 0, 0) };
    private readonly CheckBox _enabled = new() { Content = "Enabled", Margin = new Thickness(0, 4, 0, 0) };
    private readonly Border _swatch = new() { Width = 26, Height = 20, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 10, 0, 0) };
    private bool _loading;

    public IReadOnlyList<HighlightRule> Result => _rules;

    public HighlightRulesWindow(IEnumerable<HighlightRule>? current)
    {
        _rules = (current ?? Array.Empty<HighlightRule>()).Select(Clone).ToList();

        Title = "Keyword highlighting";
        Width = 660;
        Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        foreach (var (name, hex) in Backgrounds) _bg.Items.Add(new ComboBoxItem { Content = name, Tag = hex });
        foreach (var (name, hex) in Foregrounds) _fg.Items.Add(new ComboBoxItem { Content = name, Tag = hex });

        _list.SelectionChanged += (_, _) => LoadSelected();
        _pattern.TextChanged += (_, _) => ApplyFields();
        _bg.SelectionChanged += (_, _) => ApplyFields();
        _fg.SelectionChanged += (_, _) => ApplyFields();
        _ignoreCase.Checked += (_, _) => ApplyFields();
        _ignoreCase.Unchecked += (_, _) => ApplyFields();
        _regex.Checked += (_, _) => ApplyFields();
        _regex.Unchecked += (_, _) => ApplyFields();
        _bold.Checked += (_, _) => ApplyFields();
        _bold.Unchecked += (_, _) => ApplyFields();
        _enabled.Checked += (_, _) => ApplyFields();
        _enabled.Unchecked += (_, _) => ApplyFields();
        _bg.SelectionChanged += (_, _) => UpdateSwatch();
        _fg.SelectionChanged += (_, _) => UpdateSwatch();

        Content = BuildLayout();
        RebuildList(0);
        Loaded += (_, _) => { if (_rules.Count > 0) _list.SelectedIndex = 0; };
    }

    private static HighlightRule Clone(HighlightRule rule) => new()
    {
        Pattern = rule.Pattern,
        Background = rule.Background,
        Foreground = rule.Foreground,
        IgnoreCase = rule.IgnoreCase,
        IsRegex = rule.IsRegex,
        Bold = rule.Bold,
        Enabled = rule.Enabled
    };

    private UIElement BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(DialogChrome.Heading("Keyword highlighting"));
        heading.Children.Add(new TextBlock
        {
            Text = "Rules run top to bottom; the first rule to claim a character owns it. " +
                   "Matching is against the visible row, so it follows the text through reflow and scrolling.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 0, 0, 14)
        });
        root.Children.Add(heading);

        var body = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = "Rules", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        left.Children.Add(_list);

        var buttons = new UniformGrid { Columns = 2, Rows = 2, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(Small("Add", AddRule));
        buttons.Children.Add(Small("Remove", RemoveRule));
        buttons.Children.Add(Small("Up", () => Move(-1)));
        buttons.Children.Add(Small("Down", () => Move(1)));
        left.Children.Add(buttons);
        left.Children.Add(Small("Add sensible defaults", AddDefaults, 0, new Thickness(0, 8, 0, 0)));
        Grid.SetColumn(left, 0);
        body.Children.Add(left);

        var right = new StackPanel();
        right.Children.Add(new TextBlock { Text = "Pattern", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        right.Children.Add(_pattern);

        var colours = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var bgBox = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        bgBox.Children.Add(new TextBlock { Text = "Background", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        bgBox.Children.Add(_bg);
        var fgBox = new StackPanel();
        fgBox.Children.Add(new TextBlock { Text = "Text", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        fgBox.Children.Add(_fg);
        colours.Children.Add(bgBox);
        colours.Children.Add(fgBox);
        colours.Children.Add(_swatch);
        _swatch.Margin = new Thickness(8, 22, 0, 0);
        _swatch.BorderBrush = (Brush)Application.Current.FindResource("Brush.Border");
        right.Children.Add(colours);

        right.Children.Add(_ignoreCase);
        right.Children.Add(_regex);
        right.Children.Add(_bold);
        right.Children.Add(_enabled);

        _hint.Text = "Plain text matches anywhere on the row. A regular expression is matched with a " +
                     "120 ms timeout, and an invalid pattern is ignored rather than thrown.";
        _hint.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        right.Children.Add(_hint);
        Grid.SetColumn(right, 2);
        body.Children.Add(right);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(Small("Cancel", () => { DialogResult = false; }, 90));
        var ok = new Button
        {
            Content = "Save rules",
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            Width = 120,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        ok.Click += (_, _) => { SaveSelected(); DialogResult = true; };
        footer.Children.Add(ok);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private static Button Small(string text, Action action, double width = 0, Thickness? margin = null)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            Margin = margin ?? new Thickness(0)
        };
        if (width > 0) button.Width = width;
        button.Click += (_, _) => action();
        return button;
    }

    private void RebuildList(int select)
    {
        var previous = _loading;
        _loading = true;
        _list.Items.Clear();
        foreach (var rule in _rules)
        {
            var prefix = rule.Enabled ? "" : "off · ";
            var kind = rule.IsRegex ? "re:" : "";
            _list.Items.Add($"{prefix}{kind}{rule.Pattern}  —  {ColourName(rule.Background, Backgrounds, "no wash")} / " +
                            $"{ColourName(rule.Foreground, Foregrounds, "keep text")}");
        }
        if (_rules.Count > 0)
            _list.SelectedIndex = Math.Clamp(select, 0, _rules.Count - 1);
        _loading = previous;
        LoadSelected();
    }

    private static string ColourName(string hex, (string Name, string Hex)[] palette, string fallback)
    {
        foreach (var (name, value) in palette)
            if (string.Equals(value, hex ?? "", StringComparison.OrdinalIgnoreCase)) return name.ToLowerInvariant();
        return string.IsNullOrEmpty(hex) ? fallback : hex;
    }

    private void LoadSelected()
    {
        var index = _list.SelectedIndex;
        var rule = index >= 0 && index < _rules.Count ? _rules[index] : null;
        _loading = true;
        if (rule is null)
        {
            _pattern.Text = "";
            _bg.SelectedIndex = 0;
            _fg.SelectedIndex = 0;
            _ignoreCase.IsChecked = true;
            _regex.IsChecked = false;
            _bold.IsChecked = false;
            _enabled.IsChecked = true;
            _pattern.IsEnabled = _bg.IsEnabled = _fg.IsEnabled = false;
            _ignoreCase.IsEnabled = _regex.IsEnabled = _bold.IsEnabled = _enabled.IsEnabled = false;
        }
        else
        {
            _pattern.Text = rule.Pattern;
            SelectHex(_bg, Backgrounds, rule.Background);
            SelectHex(_fg, Foregrounds, rule.Foreground);
            _ignoreCase.IsChecked = rule.IgnoreCase;
            _regex.IsChecked = rule.IsRegex;
            _bold.IsChecked = rule.Bold;
            _enabled.IsChecked = rule.Enabled;
            _pattern.IsEnabled = _bg.IsEnabled = _fg.IsEnabled = true;
            _ignoreCase.IsEnabled = _regex.IsEnabled = _bold.IsEnabled = _enabled.IsEnabled = true;
        }
        _loading = false;
        UpdateSwatch();
    }

    private static void SelectHex(ComboBox box, (string Name, string Hex)[] palette, string hex)
    {
        var index = 0;
        for (var i = 0; i < palette.Length; i++)
            if (string.Equals(palette[i].Hex, hex ?? "", StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        box.SelectedIndex = index;
    }

    private void ApplyFields()
    {
        if (_loading || _list.SelectedIndex < 0 || _list.SelectedIndex >= _rules.Count) return;
        var rule = _rules[_list.SelectedIndex];
        rule.Pattern = _pattern.Text;
        rule.Background = (string?)((_bg.SelectedItem as ComboBoxItem)?.Tag) ?? "";
        rule.Foreground = (string?)((_fg.SelectedItem as ComboBoxItem)?.Tag) ?? "";
        rule.IgnoreCase = _ignoreCase.IsChecked == true;
        rule.IsRegex = _regex.IsChecked == true;
        rule.Bold = _bold.IsChecked == true;
        rule.Enabled = _enabled.IsChecked == true;
        UpdateSwatch();
    }

    private void UpdateSwatch()
    {
        var bg = HighlighterColour((string?)((_bg.SelectedItem as ComboBoxItem)?.Tag));
        var fg = HighlighterColour((string?)((_fg.SelectedItem as ComboBoxItem)?.Tag));
        _swatch.Background = new SolidColorBrush(bg ?? Colors.Transparent);
        _swatch.BorderBrush = new SolidColorBrush(fg ?? ((SolidColorBrush)Application.Current.FindResource("Brush.Border")).Color);
    }

    private static Color? HighlighterColour(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return null; }
    }

    private void SaveSelected()
    {
        ApplyFields();
    }

    private void AddRule()
    {
        _rules.Add(new HighlightRule { Pattern = "ERROR", Background = "#5A1F1F", Foreground = "#FF8A8A" });
        RebuildList(_rules.Count - 1);
    }

    private void AddDefaults()
    {
        (string Pattern, string Background, string Foreground)[] defaults =
        {
            ("ERROR", "#5A1F1F", "#FF8A8A"),
            ("FATAL", "#5A1F1F", "#FF8A8A"),
            ("denied", "#5A441F", "#FFD479"),
            ("WARNING", "#5A441F", "#FFD479"),
            ("DEPRECATED", "#3A2050", "#DFA9FF")
        };
        foreach (var (pattern, bg, fg) in defaults)
        {
            if (_rules.Any(r => string.Equals(r.Pattern, pattern, StringComparison.OrdinalIgnoreCase))) continue;
            _rules.Add(new HighlightRule { Pattern = pattern, Background = bg, Foreground = fg });
        }
        RebuildList(_rules.Count - 1);
    }

    private void RemoveRule()
    {
        var index = _list.SelectedIndex;
        if (index < 0 || index >= _rules.Count) return;
        _rules.RemoveAt(index);
        RebuildList(Math.Min(index, _rules.Count - 1));
    }

    private void Move(int delta)
    {
        var index = _list.SelectedIndex;
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _rules.Count) return;
        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        RebuildList(target);
    }
}
