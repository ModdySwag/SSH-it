using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;

namespace SshIt;

/// <summary>Split panes: two terminals side by side or stacked inside one tab.</summary>
public partial class MainWindow
{
    private void SplitRight_Click(object sender, RoutedEventArgs e) => SplitPane(vertical: true);
    private void SplitDown_Click(object sender, RoutedEventArgs e) => SplitPane(vertical: false);

    private void ClosePane_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not null) ClosePane(_active, _active.Pane);
    }

    private void PaneFocusChanged(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TerminalPane pane) return;
        var entry = _tabs.FirstOrDefault(t => t.Panes.Contains(pane));
        if (entry is null) return;
        entry.Pane = pane;
        UpdateStatus();
    }

    private void SplitPane(bool vertical)
    {
        if (_active is null) return;
        var entry = _active;
        if (entry.Panes.Count >= 2)
        {
            StatusMessage.Text = "This tab already has two panes.";
            return;
        }

        var second = new TerminalPane(entry.Session, _store.Settings);
        AttachBroadcast(second);
        second.StateChanged += _ => Dispatcher.BeginInvoke(() => { UpdateTabHeader(second); UpdateStatus(); });
        second.DiagnoseRequested += (p, context) =>
            new Views.DiagnoseWindow(p.Session, context) { Owner = this }.ShowDialog();
        second.ApproveRequested += p => ApproveFor(p);
        second.TitleChanged += (p, title) => Dispatcher.BeginInvoke(() =>
        {
            if (string.IsNullOrWhiteSpace(title) || title.Contains(".exe", StringComparison.OrdinalIgnoreCase)) return;
            entry.TitleText.Text = title;
            UpdateTitleText();
        });
        second.Notice += (p, what) =>
        {
            if (what == "close") ClosePane(entry, p);
        };

        entry.Panes.Add(second);
        entry.SplitVertical = vertical;
        entry.Pane = second;

        // Only the new pane: the loop ran over every pane on each split and accumulated one handler
        // per pane per split on a control that lives as long as the tab.
        second.GotKeyboardFocus += PaneFocusChanged;

        RebuildHost(entry);
        UpdateBroadcastMenu();
        second.Connect(CredentialVault.GetSessionSecret(entry.Session.Id));
        second.FocusTerminal();
        UpdateStatus();
    }

    private void ClosePane(TabEntry entry, TerminalPane pane)
    {
        if (pane.IsLocked)
        {
            StatusMessage.Text = "Unlock the session before closing it.";
            return;
        }
        if (entry.Panes.Count <= 1)
        {
            CloseTab(entry);
            return;
        }

        entry.Panes.Remove(pane);
        pane.Dispose();
        entry.Pane = entry.Panes[0];
        if (entry.Panes.Count == 1) SetBroadcast(entry, false);
        RebuildHost(entry);
        entry.Pane.FocusTerminal();
        UpdateStatus();
    }

    private void RebuildHost(TabEntry entry)
    {
        // Detach the panes from the previous grid before re-parenting them.
        entry.Host.Children.Clear();

        var host = new Grid();

        if (entry.Panes.Count == 1)
        {
            host.Children.Add(entry.Panes[0]);
        }
        else if (entry.SplitVertical)
        {
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var splitter = new GridSplitter
            {
                Width = 5,
                Background = (Brush)Application.Current.FindResource("Brush.Border"),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Columns
            };

            Grid.SetColumn(entry.Panes[0], 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(entry.Panes[1], 2);
            host.Children.Add(entry.Panes[0]);
            host.Children.Add(splitter);
            host.Children.Add(entry.Panes[1]);
        }
        else
        {
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var splitter = new GridSplitter
            {
                Height = 5,
                Background = (Brush)Application.Current.FindResource("Brush.Border"),
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ResizeDirection = GridResizeDirection.Rows
            };

            Grid.SetRow(entry.Panes[0], 0);
            Grid.SetRow(splitter, 1);
            Grid.SetRow(entry.Panes[1], 2);
            host.Children.Add(entry.Panes[0]);
            host.Children.Add(splitter);
            host.Children.Add(entry.Panes[1]);
        }

        entry.Host = host;
        if (_active == entry) TerminalHost.Content = host;
    }

    // ---------------------------------------------------------------- broadcast input

    private void Broadcast_Click(object sender, RoutedEventArgs e) => SetBroadcast(_active, MenuBroadcast.IsChecked);

    private void SetBroadcast(TabEntry? entry, bool on)
    {
        if (entry is null) { UpdateBroadcastMenu(); return; }

        if (on && entry.Panes.Count < 2)
        {
            MenuBroadcast.IsChecked = false;
            StatusMessage.Text = "Split the tab first — broadcast needs two panes.";
            return;
        }

        entry.Broadcast = on;
        foreach (var pane in entry.Panes) pane.ShowBroadcastBadge(on);
        StatusMessage.Text = on
            ? "Broadcasting — keystrokes go to every pane in this tab."
            : "Broadcast off.";
        UpdateBroadcastMenu();
    }

    private void UpdateBroadcastMenu()
    {
        var live = _active is not null && _active.Panes.Count > 1;
        MenuBroadcast.IsEnabled = live;
        MenuBroadcast.IsChecked = live && _active!.Broadcast;
    }

    // ---------------------------------------------------------------- send to every pane

    private void SendToAll_Click(object sender, RoutedEventArgs e) => SendToAllInteractive();

    /// <summary>
    /// Builds the target list, shows where the command will go, then writes it once per pane. SendRaw is
    /// the same primitive broadcast uses — straight to the pty, never re-raised — and a locked pane
    /// refuses it inside the pane, so the lock holds even if the plan is stale.
    /// </summary>
    private void SendToAllInteractive()
    {
        var pairs = CollectFanout();
        if (pairs.Count == 0)
        {
            StatusMessage.Text = "Send to every pane: no tabs are open.";
            return;
        }

        var dialog = new Views.SendAllWindow(pairs.Select(pair => pair.Target).ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var bytes = Fanout.CommandBytes(dialog.Command);
        var sent = 0;
        foreach (var (target, pane) in pairs)
        {
            if (!target.WillReceive) continue;
            // A pane can go away (or be locked) while the dialog is open; count what actually landed.
            if (pane.SendRaw(bytes)) sent++;
        }

        var message = sent == 1 ? "Sent to 1 pane." : "Sent to " + sent + " panes.";
        StatusMessage.Text = message;
        Announcer.Announce(StatusAnnounce, message);
    }

    private List<(FanoutTarget Target, TerminalPane Pane)> CollectFanout()
    {
        var pairs = new List<(FanoutTarget, TerminalPane)>();
        foreach (var tab in _tabs)
        {
            for (var i = 0; i < tab.Panes.Count; i++)
            {
                var pane = tab.Panes[i];
                pairs.Add((new FanoutTarget
                {
                    Tab = tab.TitleText.Text,
                    Session = pane.Session.Name,
                    Connected = pane.State == SessionState.Connected,
                    Locked = pane.IsLocked,
                    Pane = tab.Panes.Count > 1 ? $"pane {i + 1} of {tab.Panes.Count}" : ""
                }, pane));
            }
        }
        return pairs;
    }

    /// <summary>
    /// Mirrors keystrokes into the other panes of the same tab. SendRaw is used on
    /// purpose: it writes straight to the pty and does not re-raise UserInput, so a
    /// broadcast can never feed itself.
    /// </summary>
    private void AttachBroadcast(TerminalPane pane)
    {
        pane.UserInput += bytes =>
        {
            var entry = _tabs.FirstOrDefault(t => t.Panes.Contains(pane));
            if (entry is null || !entry.Broadcast || entry.Panes.Count < 2) return;
            foreach (var sibling in entry.Panes)
            {
                if (!ReferenceEquals(sibling, pane)) sibling.SendRaw(bytes);
            }
        };
    }
}
