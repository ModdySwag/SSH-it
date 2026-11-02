using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>Set the password for locking a pane, and choose whether output stays readable.</summary>
public sealed class LockSessionWindow : Window
{
    private readonly PasswordBox _password = new();
    private readonly PasswordBox _confirm = new();
    private readonly CheckBox _hide = new() { Content = "Hide the terminal output while locked", Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _error = new() { FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };

    public string Password { get; private set; } = "";
    public bool HideOutput { get; private set; }

    public LockSessionWindow(string sessionName)
    {
        Title = "Lock session";
        Width = 460;
        // Size to content: a fixed height clipped the Cancel/Lock buttons at 125% DPI.
        SizeToContent = SizeToContent.Height;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(DialogChrome.Heading("Lock this session"));
        stack.Children.Add(new TextBlock
        {
            Text = $"Typing, pasting and closing are blocked for {sessionName} until it is unlocked. " +
                   "The session keeps running and keeps its scrollback.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 0, 0, 14)
        });
        stack.Children.Add(new TextBlock { Text = "Password", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        System.Windows.Automation.AutomationProperties.SetName(_password, "Lock password");
        stack.Children.Add(_password);
        stack.Children.Add(new TextBlock { Text = "Repeat password", FontSize = 12, Margin = new Thickness(0, 10, 0, 4) });
        System.Windows.Automation.AutomationProperties.SetName(_confirm, "Repeat lock password");
        stack.Children.Add(_confirm);
        stack.Children.Add(_hide);
        _error.Foreground = (Brush)Application.Current.FindResource("Brush.Danger");
        stack.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 32 };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        var ok = new Button
        {
            Content = "Lock",
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            Width = 110,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0)
        };
        ok.Click += (_, _) => Accept();
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        Content = stack;
        Loaded += (_, _) => _password.Focus();
    }

    private void Accept()
    {
        if (_password.Password.Length < 4)
        {
            _error.Text = "Use at least four characters.";
            return;
        }
        if (_password.Password != _confirm.Password)
        {
            _error.Text = "The passwords do not match.";
            return;
        }
        Password = _password.Password;
        HideOutput = _hide.IsChecked == true;
        DialogResult = true;
    }
}

/// <summary>Ask for the password until it verifies, or the user cancels.</summary>
public sealed class UnlockSessionWindow : Window
{
    private readonly PasswordBox _password = new();
    private readonly TextBlock _error = new() { FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Func<string, bool> _verify;

    public UnlockSessionWindow(string sessionName, Func<string, bool> verify)
    {
        _verify = verify;
        Title = "Unlock session";
        Width = 420;
        Height = 280;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var stack = new StackPanel { Margin = new Thickness(22) };
        stack.Children.Add(DialogChrome.Heading("Session locked"));
        stack.Children.Add(new TextBlock
        {
            Text = sessionName + " cannot be typed into until it is unlocked.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(0, 0, 0, 14)
        });
        stack.Children.Add(new TextBlock { Text = "Password", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        System.Windows.Automation.AutomationProperties.SetName(_password, "Unlock password");
        stack.Children.Add(_password);
        _error.Foreground = (Brush)Application.Current.FindResource("Brush.Danger");
        stack.Children.Add(_error);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 32 };
        cancel.Click += (_, _) => DialogResult = false;
        buttons.Children.Add(cancel);
        var ok = new Button
        {
            Content = "Unlock",
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            Width = 110,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = true
        };
        ok.Click += (_, _) => Attempt();
        buttons.Children.Add(ok);
        stack.Children.Add(buttons);

        _password.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Attempt(); };
        Content = stack;
        Loaded += (_, _) => _password.Focus();
    }

    private void Attempt()
    {
        if (_verify(_password.Password))
        {
            DialogResult = true;
            return;
        }
        _error.Text = "That password does not match.";
        _password.Clear();
        _password.Focus();
    }
}
