using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;

namespace SshIt.Views;

public partial class SettingsWindow : Window
{
    private readonly DataStore _store;

    public SettingsWindow(DataStore store)
    {
        InitializeComponent();
        _store = store;
        var s = store.Settings;

        foreach (var (id, label) in ThemeCatalog.MenuEntries)
            ThemeBox.Items.Add(new ComboBoxItem { Content = label, Tag = id });
        SelectTag(ThemeBox, s.Theme);
        foreach (var theme in TerminalTheme.BuiltIn)
        {
            var item = new ComboBoxItem { Content = theme.Name, Tag = theme.Name };
            TerminalThemeBox.Items.Add(item);
        }
        SelectTag(TerminalThemeBox, s.TerminalTheme);

        FontBox.Text = s.FontFamily;
        FontSizeBox.Text = s.FontSize.ToString("0.#");
        SelectTag(CursorBox, s.CursorStyle == "Block" ? "0" : s.CursorStyle == "Bar" ? "1" : "2");
        BlinkCheck.IsChecked = s.CursorBlink;

        ConfirmCloseCheck.IsChecked = s.ConfirmOnClose;
        CloseToTrayCheck.IsChecked = s.CloseToTray;
        MinimizeToTrayCheck.IsChecked = s.MinimizeToTray;
        ReopenCheck.IsChecked = s.ReopenTabs;
        MultiPasteCheck.IsChecked = s.WarnOnMultiPaste;
        BellCheck.IsChecked = s.BellSound;
        LogCheck.IsChecked = s.LogSessions;
        RecordCheck.IsChecked = s.RecordSessions;
        AgentGateCheck.IsChecked = s.AgentGate;

        ScrollbackBox.Text = s.ScrollbackLines.ToString();
        CopyOnSelectCheck.IsChecked = s.CopyOnSelect;

        DataPathBox.Text = store.RootDir;
        PortableText.Text = store.Portable
            ? "Portable mode: data lives next to the executable."
            : "Installed mode: data lives in %APPDATA%\\SSH-it. Copy a file named Conduit.portable next to SSH-it.exe to switch to portable mode.";

        _highlightRules = s.HighlightRules.Select(Clone).ToList();
        UpdateHighlightSummary();
    }

    private List<HighlightRule> _highlightRules = new();

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

    private void UpdateHighlightSummary()
    {
        HighlightSummary.Text = _highlightRules.Count == 0
            ? "No rules yet. Highlighting is off until you add one — try \"Add sensible defaults\" for ERROR, FATAL, denied and WARNING."
            : _highlightRules.Count + (_highlightRules.Count == 1 ? " rule: " : " rules: ") +
              string.Join(", ", _highlightRules.Select(r => r.Pattern));
    }

    private void OpenHighlighting_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new HighlightRulesWindow(_highlightRules) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _highlightRules = dialog.Result.Select(Clone).ToList();
        UpdateHighlightSummary();
    }

    private static void SelectTag(ComboBox box, string tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem item && string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string? TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString();

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(_store.RootDir) { UseShellExecute = true }); }
        catch { }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _store.Settings;
        s.Theme = TagOf(ThemeBox) ?? "System";
        s.TerminalTheme = TagOf(TerminalThemeBox) ?? "SSH-it Dark";
        s.FontFamily = string.IsNullOrWhiteSpace(FontBox.Text) ? "Cascadia Mono" : FontBox.Text.Trim();
        s.FontSize = double.TryParse(FontSizeBox.Text, out var size) ? Math.Clamp(size, 8, 32) : 13;
        s.CursorStyle = TagOf(CursorBox) switch { "1" => "Bar", "2" => "Underline", _ => "Block" };
        s.CursorBlink = BlinkCheck.IsChecked == true;

        s.ConfirmOnClose = ConfirmCloseCheck.IsChecked == true;
        s.CloseToTray = CloseToTrayCheck.IsChecked == true;
        s.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        s.ReopenTabs = ReopenCheck.IsChecked == true;
        s.WarnOnMultiPaste = MultiPasteCheck.IsChecked == true;
        s.BellSound = BellCheck.IsChecked == true;
        s.LogSessions = LogCheck.IsChecked == true;
        s.RecordSessions = RecordCheck.IsChecked == true;
        s.AgentGate = AgentGateCheck.IsChecked == true;

        s.ScrollbackLines = int.TryParse(ScrollbackBox.Text, out var lines) ? Math.Clamp(lines, 100, 200000) : 10000;
        s.CopyOnSelect = CopyOnSelectCheck.IsChecked == true;
        s.HighlightRules = _highlightRules;

        _store.SaveSettings();
        ((App)Application.Current).Theme.Apply(s.Theme);
        DialogResult = true;
    }
}
