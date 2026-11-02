using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// R3-06: approve ONE exact command for the agent surface, bound to the pane it is approved in. The
/// approval authorises that command and nothing else, until it expires or the pane's pty exits —
/// closing the pane ends it, which is what makes it an approval for a live session rather than a
/// permanent grant.
/// </summary>
public sealed class ApproveWindow : Window
{
    private readonly Session _session;
    private readonly int _pane;
    private readonly DateTime? _started;
    private readonly TextBox _command = new();
    private readonly TextBox _minutes = new();
    private readonly TextBlock _status = new();
    private readonly Button _approve = new();

    public ApproveWindow(Session session, string? lastCommand, int panePid, DateTime? paneStarted)
    {
        _session = session;
        _pane = panePid;
        _started = paneStarted;

        Title = "Approve a command for agents — " + session.DisplayTarget;
        Width = 660;
        Height = 340;
        MinWidth = 560;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        var heading = new TextBlock
        {
            Text = "One exact command, for the agent surface, on " + session.DisplayTarget + ".",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(14, 14, 14, 4)
        };

        var explain = new TextBlock
        {
            Text = "The approval authorises this command and nothing else. It ends when the minutes run out "
                   + "or when this pane closes — either way the agent surface is back to its lease.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Margin = new Thickness(14, 0, 14, 10)
        };

        _command.Text = lastCommand ?? "";
        _command.Height = 28;
        _command.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_command, "ApproveCommand");
        System.Windows.Automation.AutomationProperties.SetName(_command, "Command to approve");

        _minutes.Text = "5";
        _minutes.Width = 64;
        _minutes.Height = 28;
        _minutes.TextAlignment = TextAlignment.Center;
        System.Windows.Automation.AutomationProperties.SetAutomationId(_minutes, "ApproveMinutes");
        System.Windows.Automation.AutomationProperties.SetName(_minutes, "Minutes until the approval expires");

        var commandRow = new DockPanel { Margin = new Thickness(14, 0, 14, 8), LastChildFill = true };
        var commandLabel = new TextBlock { Text = "Command", Width = 78, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(commandLabel, Dock.Left);
        commandRow.Children.Add(commandLabel);
        commandRow.Children.Add(_command);

        var minutesRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(14, 0, 14, 8) };
        minutesRow.Children.Add(new TextBlock { Text = "Minutes", Width = 78, VerticalAlignment = VerticalAlignment.Center });
        minutesRow.Children.Add(_minutes);
        minutesRow.Children.Add(new TextBlock
        {
            Text = "1–" + AgentLease.MaxMinutes,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        });

        var ptyLine = new TextBlock
        {
            Text = panePid > 0
                ? "Bound to this pane's pty: pid " + panePid
                  + (paneStarted is null ? "" : ", started " + paneStarted.Value.ToLocalTime().ToString("HH:mm:ss"))
                : "This pane has no live pty, so nothing can be bound to it — connect the session first.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 2, 14, 8),
            Foreground = (Brush)Application.Current.FindResource(
                panePid > 0 ? "Brush.TextMuted" : "Brush.Danger")
        };

        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(14, 0, 14, 8);
        _status.Text = "";

        _approve.Content = "Approve";
        _approve.Height = 28;
        _approve.MinWidth = 96;
        _approve.Padding = new Thickness(14, 0, 14, 0);
        _approve.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _approve.IsEnabled = panePid > 0;
        _approve.Click += (_, _) => Approve();

        var close = new Button
        {
            Content = "Close",
            Height = 28,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar"),
            IsCancel = true
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(14, 0, 14, 14)
        };
        buttons.Children.Add(_approve);
        buttons.Children.Add(close);

        var body = new StackPanel();
        body.Children.Add(heading);
        body.Children.Add(explain);
        body.Children.Add(commandRow);
        body.Children.Add(minutesRow);
        body.Children.Add(ptyLine);
        body.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(body);
        Content = root;
        Loaded += (_, _) => _command.Focus();
    }

    private void Approve()
    {
        var command = _command.Text.Trim();
        if (command.Length == 0)
        {
            Fail("Type the exact command to approve.");
            return;
        }

        if (!int.TryParse(_minutes.Text.Trim(), out var minutes) || minutes < 1 || minutes > AgentLease.MaxMinutes)
        {
            Fail("Minutes must be 1–" + AgentLease.MaxMinutes + ".");
            return;
        }

        if (_pane <= 0 || _started is null)
        {
            Fail("This pane has no live pty to bind the approval to.");
            return;
        }

        try
        {
            var grant = AgentLease.Grant(_session, new[] { "exec" }, minutes, command, null, (_pane, _started.Value));
            _status.Text = "Approval " + grant.Id + " until " + grant.Expires.ToLocalTime().ToString("HH:mm")
                           + " — bound to pid " + _pane + ", so closing this pane ends it.";
            _status.Foreground = (Brush)Application.Current.FindResource("Brush.Success");
            _approve.IsEnabled = false;
        }
        catch (Exception ex)
        {
            Fail("No approval: " + ex.Message);
        }
    }

    private void Fail(string message)
    {
        _status.Text = message;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.Danger");
    }
}
