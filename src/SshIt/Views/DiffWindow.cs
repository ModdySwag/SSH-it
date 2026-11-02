using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// R3-10: run one command on one or two saved hosts and align the outputs side by side. The
/// delta column says "=" for a shared line, "≠" for a replaced one, "+" for a line only the right
/// side printed and "−" for one only the left side did.
/// </summary>
public sealed class DiffWindow : Window
{
    private readonly ComboBox _left = new();
    private readonly ComboBox _right = new();
    private readonly TextBox _command = new();
    private readonly ListView _rows = new();
    private readonly TextBlock _status = new();
    private readonly Button _run = new();
    private readonly Button _swap = new();

    public DiffWindow(IReadOnlyList<Session> sessions, Session? active, string? lastCommand)
    {
        Title = "Compare command output";
        Width = 1180;
        Height = 720;
        MinWidth = 820;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        var hosts = FleetPosture.Scannable(sessions).Select(s => new HostChoice(s)).ToList();

        _left.Width = 200;
        _left.Height = 26;
        _left.ItemsSource = hosts;
        _left.SelectedItem = hosts.FirstOrDefault(c => active is not null && c.Session.Id == active.Id)
                             ?? hosts.FirstOrDefault();
        System.Windows.Automation.AutomationProperties.SetAutomationId(_left, "DiffLeft");
        System.Windows.Automation.AutomationProperties.SetName(_left, "Left host");

        _right.Width = 200;
        _right.Height = 26;
        _right.Margin = new Thickness(6, 0, 0, 0);
        _right.ItemsSource = hosts;
        _right.SelectedItem = _left.SelectedItem;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_right, "DiffRight");
        System.Windows.Automation.AutomationProperties.SetName(_right, "Right host");

        _swap.Content = "⇄";
        _swap.Width = 30;
        _swap.Height = 26;
        _swap.Margin = new Thickness(6, 0, 0, 0);
        _swap.ToolTip = "Swap the two hosts.";
        _swap.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _swap.Click += (_, _) =>
        {
            var left = _left.SelectedItem;
            _left.SelectedItem = _right.SelectedItem;
            _right.SelectedItem = left;
        };

        _command.Text = lastCommand ?? "";
        _command.Height = 26;
        _command.Margin = new Thickness(12, 0, 0, 0);
        _command.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_command, "DiffCommand");
        System.Windows.Automation.AutomationProperties.SetName(_command, "Command to compare");

        _run.Content = "Run both";
        _run.Height = 26;
        _run.Padding = new Thickness(14, 0, 14, 0);
        _run.Margin = new Thickness(8, 0, 0, 0);
        _run.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _run.Click += (_, _) => _ = RunDiff();

        var close = new Button
        {
            Content = "Close",
            Height = 26,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar"),
            IsCancel = true
        };

        _status.FontSize = 11;
        _status.Margin = new Thickness(14, 0, 14, 4);
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _status.Text = hosts.Count == 0
            ? "No saved SSH sessions to compare."
            : "Pick two hosts (or the same one twice), type a command, then Run both.";

        // Right-docked controls are added right to left, and the command box is added last so it
        // takes the fill (DockPanel lays out in child order — see the Watch window's trap).
        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 6), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_run, Dock.Right);
        DockPanel.SetDock(_command, Dock.Left);
        DockPanel.SetDock(_swap, Dock.Left);
        DockPanel.SetDock(_right, Dock.Left);
        DockPanel.SetDock(_left, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(_run);
        header.Children.Add(_command);
        header.Children.Add(_swap);
        header.Children.Add(_right);
        header.Children.Add(_left);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "", Width = 30, DisplayMemberBinding = new System.Windows.Data.Binding("Mark") });
        view.Columns.Add(new GridViewColumn { Header = "Left", Width = 520, DisplayMemberBinding = new System.Windows.Data.Binding("Left") });
        view.Columns.Add(new GridViewColumn { Header = "Right", Width = 520, DisplayMemberBinding = new System.Windows.Data.Binding("Right") });
        _rows.View = view;
        _rows.BorderThickness = new Thickness(0);
        _rows.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _rows.FontSize = 12;
        _rows.Margin = new Thickness(14, 0, 14, 8);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_status);
        root.Children.Add(_rows);
        Content = root;

        if (hosts.Count == 0) _run.IsEnabled = false;
        Loaded += (_, _) => _command.Focus();
    }

    private async Task RunDiff()
    {
        if (_left.SelectedItem is not HostChoice firstChoice) return;
        var first = firstChoice.Session;
        var second = (_right.SelectedItem as HostChoice ?? firstChoice).Session;
        var command = _command.Text.Trim();
        if (command.Length == 0)
        {
            _status.Text = "Type a command to compare.";
            return;
        }

        _run.IsEnabled = false;
        _rows.Items.Clear();
        _status.Text = "Running on " + first.DisplayTarget + (ReferenceEquals(first, second) ? " twice…" : " and " + second.DisplayTarget + "…");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var leftTask = BlockDiff.RunAsync(first, command, cts.Token);
        var rightTask = BlockDiff.RunAsync(second, command, cts.Token);
        var left = await leftTask;
        var right = await rightTask;

        var rows = BlockDiff.Align(left.Output, right.Output);
        _rows.ItemsSource = rows;
        _status.Text = BlockDiff.Verdict(left, right, rows)
                       + $" · {first.DisplayTarget} exit {left.Exit} ({left.Milliseconds} ms)"
                       + $" · {second.DisplayTarget} exit {right.Exit} ({right.Milliseconds} ms)";

        if (left.Error.Trim().Length > 0 || right.Error.Trim().Length > 0)
        {
            var error = (left.Error + " " + right.Error).Trim().Replace("\r", " ").Replace("\n", " | ");
            _status.Text += " · " + (error.Length > 160 ? error[..160] + "…" : error);
        }

        _run.IsEnabled = true;
    }

    /// <summary>
    /// A picker item that prints its own name. The theme's ComboBox template supplies an
    /// ItemTemplate, which makes DisplayMemberPath a no-op — binding Session objects directly
    /// showed "SshIt.Models.Session" in both boxes.
    /// </summary>
    private sealed class HostChoice
    {
        public HostChoice(Session session) => Session = session;
        public Session Session { get; }
        public override string ToString() => Session.DisplayTarget;
    }
}
