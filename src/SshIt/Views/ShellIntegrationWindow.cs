using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Services;
using SshIt.Terminal;

namespace SshIt.Views;

/// <summary>
/// The shell-integration snippet, one shell at a time. Bash was the only text for a long time;
/// zsh and fish now carry the same OSC 133 contract through their own hooks, and the picker is
/// what makes that discoverable instead of a paragraph in the docs.
/// </summary>
public sealed class ShellIntegrationWindow : Window
{
    private readonly ComboBox _shell = new() { Width = 140 };
    private readonly TextBlock _description = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
        Margin = new Thickness(0, 8, 0, 10)
    };
    private readonly TextBox _box = new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono"),
        FontSize = 12,
        Padding = new Thickness(12),
        Background = (Brush)Application.Current.FindResource("Brush.TerminalBackground"),
        Foreground = (Brush)Application.Current.FindResource("Brush.TerminalForeground"),
        BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
        BorderThickness = new Thickness(1)
    };
    private readonly TerminalPane? _pane;

    public ShellIntegrationWindow(TerminalPane? pane)
    {
        _pane = pane;
        Title = "Shell integration";
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        foreach (var shell in ShellIntegration.Shells) _shell.Items.Add(shell);
        _shell.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(_shell, "ShellPicker");
        AutomationProperties.SetName(_shell, "Shell");
        _shell.SelectionChanged += (_, _) => RefreshSnippet();
        AutomationProperties.SetAutomationId(_box, "ShellSnippet");

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(DialogChrome.Heading("Shell integration"));

        var picker = new StackPanel { Orientation = Orientation.Horizontal };
        picker.Children.Add(new TextBlock
        {
            Text = "Shell", VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        picker.Children.Add(_shell);
        root.Children.Add(picker);
        root.Children.Add(_description);
        root.Children.Add(_box);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var send = new Button
        {
            Content = "Send to session", Style = (Style)Application.Current.FindResource("Btn.Accent"),
            Width = 140, Margin = new Thickness(0, 0, 8, 0),
            Visibility = _pane is null ? Visibility.Collapsed : Visibility.Visible
        };
        send.Click += (_, _) => { _pane?.SendCommand(_box.Text); Close(); };
        var copy = new Button { Content = "Copy", Width = 88, Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => { try { Clipboard.SetText(_box.Text); } catch { } };
        var close = new Button { Content = "Close", Width = 88, IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(send);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        Content = root;
        RefreshSnippet();
    }

    private void RefreshSnippet()
    {
        var shell = _shell.SelectedItem as string ?? "Bash";
        _box.Text = ShellIntegration.Snippet(shell);
        _description.Text = ShellIntegration.Description(shell);
        AutomationProperties.SetName(_box, shell + " snippet");
    }
}
