using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// "Send to every pane": one command, every connected pane, across every tab. The dialog exists to make
/// the fan-out visible before it happens — the list names each pane, marks the ones that will not receive
/// it and why, and the Send button refuses when nothing is left.
/// </summary>
public sealed class SendAllWindow : Window
{
    private readonly TextBox _command = new();
    private readonly Button _send;

    /// <summary>What the user typed; only meaningful when ShowDialog() returned true.</summary>
    public string Command => _command.Text.Trim();

    public SendAllWindow(IReadOnlyList<FanoutTarget> plan)
    {
        Title = "Send to every pane";
        Width = 560;
        Height = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(DialogChrome.Heading("Send to every pane"));
        stack.Children.Add(DialogChrome.Label(
            "The command is typed into each connected pane, in every tab. Nothing else is sent."));

        _command.Height = 32;
        _command.Margin = new Thickness(0, 6, 0, 14);
        _command.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_command, "FanoutCommand");
        System.Windows.Automation.AutomationProperties.SetName(_command, "Command to send to every pane");
        stack.Children.Add(_command);

        stack.Children.Add(DialogChrome.Label("Panes"));
        var list = new StackPanel { Margin = new Thickness(0, 2, 0, 12) };
        foreach (var target in plan) list.Children.Add(Row(target));
        stack.Children.Add(new ScrollViewer
        {
            Height = 150,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = list
        });

        var summary = new TextBlock
        {
            Text = Fanout.Summary(plan),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0)
        };
        stack.Children.Add(summary);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _send = new Button
        {
            Content = "Send",
            Width = 96,
            IsDefault = true,
            Style = (Style)Application.Current.FindResource("Btn.Accent"),
            IsEnabled = Fanout.CanSend(plan, _command.Text)
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_send, "FanoutSend");
        _command.TextChanged += (_, _) => _send.IsEnabled = Fanout.CanSend(plan, _command.Text);
        _send.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_command.Text)) return;
            DialogResult = true;
        };

        var cancel = new Button { Content = "Cancel", Width = 96, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        buttons.Children.Add(_send);
        buttons.Children.Add(cancel);
        stack.Children.Add(buttons);

        Content = stack;
        Loaded += (_, _) => _command.Focus();
    }

    /// <summary>One target: a state dot, the tab, the session, and why it will not receive the command.</summary>
    private static UIElement Row(FanoutTarget target)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        row.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = (Brush)Application.Current.FindResource(
                target.WillReceive ? "Brush.Success" : "Brush.TextDisabled")
        });

        row.Children.Add(new TextBlock
        {
            Text = target.Label,
            Width = 300,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        row.Children.Add(new TextBlock
        {
            Text = target.WillReceive ? "receives it" : "skipped — " + target.State,
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource(
                target.WillReceive ? "Brush.TextMuted" : "Brush.Warn"),
            VerticalAlignment = VerticalAlignment.Center
        });

        return row;
    }
}
