using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;

namespace SshIt.Views;

// The dialogs below are deliberately built in code: they are small, uniform and
// inherit the same theme brushes as the main window without extra XAML files.

internal static class DialogChrome
{
    public static Window Create(Window? owner, string title, double width, double height, bool resizable = false)
    {
        return new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.NoResize,
            Background = (Brush)Application.Current.FindResource("Brush.Window"),
            Foreground = (Brush)Application.Current.FindResource("Brush.Text"),
            FontFamily = (FontFamily)Application.Current.FindResource("Font.UI"),
            SnapsToDevicePixels = true,
            UseLayoutRounding = true
        };
    }

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
        Margin = new Thickness(0, 0, 0, 5)
    };

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 4)
    };
}

/// <summary>Single-line text prompt and simple choice list.</summary>
public static class PromptDialog
{
    public static string? Show(Window? owner, string title, string label, string initial = "")
    {
        var window = DialogChrome.Create(owner, title, 420, 180);
        var result = (string?)null;

        var box = new TextBox { Text = initial, Height = 32, Margin = new Thickness(0, 0, 0, 16) };
        box.SelectAll();

        var ok = new Button { Content = "OK", Style = (Style)Application.Current.FindResource("Btn.Accent"), Width = 88, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; window.DialogResult = true; };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(DialogChrome.Label(label));
        stack.Children.Add(box);
        stack.Children.Add(buttons);
        window.Content = stack;
        window.Loaded += (_, _) => box.Focus();

        return window.ShowDialog() == true ? result : null;
    }

    public static int Choose(Window? owner, string title, string label, IReadOnlyList<string> options, int selected = 0)
    {
        var window = DialogChrome.Create(owner, title, 420, 260);
        var chosen = -1;

        var list = new ListBox { Height = 140, Margin = new Thickness(0, 0, 0, 16) };
        foreach (var option in options) list.Items.Add(option);
        if (selected >= 0 && selected < options.Count) list.SelectedIndex = selected;

        var ok = new Button { Content = "Move", Style = (Style)Application.Current.FindResource("Btn.Accent"), Width = 88, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) =>
        {
            chosen = list.SelectedIndex;
            window.DialogResult = true;
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(DialogChrome.Label(label));
        stack.Children.Add(list);
        stack.Children.Add(buttons);
        window.Content = stack;

        return window.ShowDialog() == true ? chosen : -1;
    }
}

/// <summary>Read-only text with copy, and optionally "send to session".</summary>
public sealed class TextWindow : Window
{
    private readonly TextBox _box;
    private TerminalPane? _pane;

    public TextWindow(string title, string text, string? description = null)
    {
        Title = title;
        Width = 660;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        _box = new TextBox
        {
            Text = text,
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

        var stack = new StackPanel { Margin = new Thickness(20) };
        if (description is not null)
            stack.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
            });

        stack.Children.Add(_box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copy", Width = 88 };
        copy.Click += (_, _) => { try { Clipboard.SetText(_box.Text); } catch { } };
        var close = new Button { Content = "Close", Width = 88, Margin = new Thickness(8, 0, 0, 0), IsCancel = true, IsDefault = true };
        var send = new Button { Content = "Send to session", Style = (Style)Application.Current.FindResource("Btn.Accent"), Width = 130, Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
        send.Click += (_, _) => { _pane?.SendCommand(_box.Text); Close(); };

        buttons.Children.Add(send);
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        stack.Children.Add(buttons);
        Content = stack;

        _sendButton = send;
    }

    private readonly Button _sendButton;

    public TextWindow WithSendToSession(TerminalPane? pane)
    {
        _pane = pane;
        _sendButton.Visibility = pane is null ? Visibility.Collapsed : Visibility.Visible;
        return this;
    }
}

/// <summary>Key list, generation and public-key copy.</summary>
public sealed class KeyManagerWindow : Window
{
    private readonly ListBox _list = new() { Height = 220 };
    private readonly TextBlock _details;

    public KeyManagerWindow()
    {
        Title = "Key manager";
        Width = 640;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        _details = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono"),
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 10, 0, 0)
        };

        var generate = new Button { Content = "Generate ed25519 key…", Height = 32 };
        generate.Click += (_, _) => Generate();
        var copy = new Button { Content = "Copy public key", Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => CopyPublic();
        var reveal = new Button { Content = "Show fingerprint", Height = 32, Margin = new Thickness(8, 0, 0, 0) };
        reveal.Click += (_, _) => ShowFingerprint();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(generate);
        buttons.Children.Add(copy);
        buttons.Children.Add(reveal);

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(DialogChrome.Heading("SSH keys"));
        stack.Children.Add(new TextBlock
        {
            Text = "Keys in " + KeyDirectory + ". Private keys never leave this folder; SSH-it only reads their public halves.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        });
        stack.Children.Add(_list);
        stack.Children.Add(buttons);
        stack.Children.Add(_details);
        Content = stack;

        _list.SelectionChanged += (_, _) => ShowFingerprint();
        Refresh();
    }

    private static string KeyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    private void Refresh()
    {
        _list.Items.Clear();
        try
        {
            if (!Directory.Exists(KeyDirectory)) return;
            foreach (var file in Directory.EnumerateFiles(KeyDirectory)
                                         .Where(f => !f.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(f => f))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("known_hosts") || name.StartsWith("config") ||
                    name.StartsWith("authorized_keys")) continue;
                var info = new FileInfo(file);
                if (info.Length == 0) continue;
                _list.Items.Add(name);
            }
        }
        catch (Exception ex) { _details.Text = ex.Message; }
    }

    private string? Selected => _list.SelectedItem as string;

    private void ShowFingerprint()
    {
        if (Selected is null) return;
        var path = Path.Combine(KeyDirectory, Selected);
        var output = RunTool("ssh-keygen.exe", $"-l -f \"{path}\"");
        _details.Text = string.IsNullOrWhiteSpace(output) ? "No fingerprint available (is this a private key?)" : output.Trim();
    }

    private void CopyPublic()
    {
        if (Selected is null) return;
        var path = Path.Combine(KeyDirectory, Selected + ".pub");
        try
        {
            if (File.Exists(path)) Clipboard.SetText(File.ReadAllText(path).Trim());
            else Clipboard.SetText(RunTool("ssh-keygen.exe", $"-y -f \"{Path.Combine(KeyDirectory, Selected)}\"").Trim());
            _details.Text = "Public key copied. Paste it into ~/.ssh/authorized_keys on the host.";
        }
        catch (Exception ex) { _details.Text = ex.Message; }
    }

    private void Generate()
    {
        var name = PromptDialog.Show(this, "Generate key", "File name (in ~/.ssh):", "id_ed25519_sshit");
        if (string.IsNullOrWhiteSpace(name)) return;
        var path = Path.Combine(KeyDirectory, name.Trim());

        var result = RunTool("ssh-keygen.exe", $"-t ed25519 -f \"{path}\" -N \"\" -C \"SSH-it\"");
        Refresh();
        _details.Text = string.IsNullOrWhiteSpace(result) ? "Key created: " + path : result.Trim();
    }

    private static string RunTool(string exe, string arguments)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", exe);
            if (!File.Exists(path)) path = exe;
            var info = new ProcessStartInfo(path, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null) return "";
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (Exception ex) { return ex.Message; }
    }
}

public sealed class AboutWindow : Window
{
    public AboutWindow()
    {
        Title = "About SSH-it";
        Width = 460;
        Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

        var stack = new StackPanel { Margin = new Thickness(26) };
        stack.Children.Add(new TextBlock
        {
            Text = "SSH-it",
            FontSize = 28,
            FontWeight = FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Version " + version,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 2, 0, 16)
        });
        stack.Children.Add(new TextBlock
        {
            Text = "A native SSH workspace for Windows. It drives the OpenSSH client that ships with Windows, " +
                   "so every key type, ProxyJump chain and ssh_config directive behaves exactly as it does in a terminal.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Sessions are plain JSON files on your disk. There is no account, no cloud and no telemetry.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            Margin = new Thickness(0, 12, 0, 0),
            LineHeight = 19
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Open source. Built with .NET and WPF.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 20, 0, 0)
        });

        var close = new Button { Content = "Close", Width = 96, HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 24, 0, 0) };
        stack.Children.Add(close);
        Content = stack;
    }
}

public sealed class ShortcutsWindow : Window
{
    public ShortcutsWindow()
    {
        Title = "Keyboard shortcuts";
        Width = 560;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var rows = new (string Keys, string Action)[]
        {
            ("Ctrl+N", "New session"),
            ("Ctrl+K", "Quick connect"),
            ("Ctrl+Shift+T", "New tab"),
            ("Ctrl+Shift+W", "Close tab"),
            ("Ctrl+Tab", "Next tab"),
            ("Alt+1…9", "Go to tab"),
            ("Ctrl+Shift+C", "Copy selection"),
            ("Ctrl+Shift+V", "Paste"),
            ("Ctrl+Shift+A", "Select all"),
            ("Ctrl+Shift+U", "Copy last command"),
            ("Ctrl+Shift+O", "Copy last output"),
            ("Ctrl+Shift+R", "Save a command receipt"),
            ("Ctrl+Shift+Up", "Jump to previous prompt"),
            ("Ctrl+Shift+Down", "Jump to next prompt"),
            ("Ctrl+F", "Find in terminal"),
            ("Ctrl+Shift+G", "Diagnose connection"),
            ("Ctrl+Shift+F", "Files panel"),
            ("Ctrl+Shift+S", "Host information"),
            ("Ctrl+Shift+P", "Command palette"),
            ("Ctrl+wheel", "Zoom the terminal"),
            ("Ctrl+Shift+wheel", "Fade the whole window"),
            ("Ctrl+=", "Zoom in"),
            ("Ctrl+-", "Zoom out"),
            ("Ctrl+0", "Reset zoom"),
            ("Ctrl+,", "Options"),
            ("F2", "Edit selected session"),
            ("Del", "Delete selected session"),
            ("F11", "Full screen"),
        };

        var grid = new Grid { Margin = new Thickness(24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < rows.Length; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < rows.Length; i++)
        {
            var keys = new TextBlock
            {
                Text = rows[i].Keys,
                FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono"),
                FontSize = 12,
                Margin = new Thickness(0, 5, 0, 5),
                Foreground = (Brush)Application.Current.FindResource("Brush.Accent")
            };
            var action = new TextBlock
            {
                Text = rows[i].Action,
                FontSize = 12.5,
                Margin = new Thickness(0, 5, 0, 5)
            };
            Grid.SetRow(keys, i);
            Grid.SetRow(action, i);
            Grid.SetColumn(action, 1);
            grid.Children.Add(keys);
            grid.Children.Add(action);
        }

        Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // A read-only list with no buttons: Esc is the only way out that does not need the mouse.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }
}

/// <summary>Reusable snippet picker.</summary>

