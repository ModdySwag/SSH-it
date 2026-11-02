using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SshIt.Views;

/// <summary>A one-question dialog: a label, a text box, OK and Cancel. Used to name a workspace.</summary>
public sealed class NamePromptWindow : Window
{
    private readonly TextBox _box = new();

    public string Value { get; private set; } = "";

    public NamePromptWindow(string title, string label, string initial)
    {
        Title = title;
        Width = 460;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        var text = new TextBlock { Text = label, Margin = new Thickness(16, 16, 16, 6) };
        _box.Text = initial;
        _box.Height = 30;
        _box.Margin = new Thickness(16, 0, 16, 12);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_box, "NamePromptBox");
        System.Windows.Automation.AutomationProperties.SetName(_box, label);
        _box.SelectAll();

        var ok = new Button
        {
            Content = "Save", Height = 28, MinWidth = 92, IsDefault = true,
            Style = (Style)Application.Current.FindResource("Btn.Accent")
        };
        ok.Click += (_, _) => Accept();

        var cancel = new Button
        {
            Content = "Cancel", Height = 28, MinWidth = 92, IsCancel = true,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar")
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 14)
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel();
        root.Children.Add(text);
        root.Children.Add(_box);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) => _box.Focus();
    }

    private void Accept()
    {
        var value = _box.Text.Trim();
        if (value.Length == 0) return;
        Value = value;
        DialogResult = true;
    }

    public static string? Ask(Window owner, string title, string label, string initial)
    {
        var dialog = new NamePromptWindow(title, label, initial) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
