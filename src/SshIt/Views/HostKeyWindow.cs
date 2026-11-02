using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// Host-key forensics for one session: what known_hosts holds, what the server offers now, whether
/// they agree, and which other hosts already trust the same key. Removing an entry is explicit and
/// leaves a timestamped backup beside the file.
/// </summary>
public sealed class HostKeyWindow : Window
{
    private readonly Session _session;
    private readonly ListView _list = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _shared = new();
    private readonly TextBlock _note = new();
    private readonly Button _forget = new();
    private HostKeyReport? _report;

    public HostKeyWindow(Session session)
    {
        _session = session;
        Title = $"Host keys — {session.Host}:{session.Port}";
        Width = 820;
        Height = 520;
        MinWidth = 620;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        var refresh = new Button
        {
            Content = "Recheck",
            Height = 28,
            Padding = new Thickness(12, 0, 12, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar")
        };
        refresh.Click += (_, _) => _ = LoadAsync();

        _forget.Content = "Forget this host’s key…";
        _forget.Height = 28;
        _forget.Margin = new Thickness(8, 0, 0, 0);
        _forget.Padding = new Thickness(12, 0, 12, 0);
        _forget.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _forget.IsEnabled = false;
        _forget.Click += (_, _) => Forget();

        var close = new Button
        {
            Content = "Close",
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar"),
            IsCancel = true
        };

        _status.FontSize = 11;
        _status.Margin = new Thickness(12, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 8) };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_forget, Dock.Right);
        DockPanel.SetDock(refresh, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(_forget);
        header.Children.Add(refresh);
        header.Children.Add(_status);

        var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Key type", Width = 150, DisplayMemberBinding = new System.Windows.Data.Binding("KeyType") });
        view.Columns.Add(new GridViewColumn { Header = "Stored in known_hosts", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding("Stored") });
        view.Columns.Add(new GridViewColumn { Header = "Offered now", Width = 260, DisplayMemberBinding = new System.Windows.Data.Binding("Current") });
        view.Columns.Add(new GridViewColumn { Header = "Verdict", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Status") });
        _list.View = view;
        _list.BorderThickness = new Thickness(0);
        _list.Margin = new Thickness(14, 0, 14, 8);

        _shared.TextWrapping = TextWrapping.Wrap;
        _shared.Margin = new Thickness(14, 0, 14, 4);
        _note.TextWrapping = TextWrapping.Wrap;
        _note.Margin = new Thickness(14, 0, 14, 10);
        _note.FontSize = 11;
        _note.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_shared, Dock.Bottom);
        DockPanel.SetDock(_note, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_shared);
        root.Children.Add(_note);
        root.Children.Add(_list);
        Content = root;

        Loaded += (_, _) => _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _forget.IsEnabled = false;
        _status.Text = "Reading known_hosts and asking the host…";
        try
        {
            var report = await Task.Run(() => HostKeys.Inspect(_session));
            _report = report;
            _list.Items.Clear();
            foreach (var verdict in report.Verdicts) _list.Items.Add(verdict);

            _status.Text = $"{report.KnownHostsPath} · {report.Stored.Count} stored · {report.Current.Count} offered";
            _forget.IsEnabled = report.Stored.Count > 0;

            var changed = report.Verdicts.Count(v => v.Status == "changed");
            var match = report.Verdicts.Count(v => v.Status == "match");
            var fresh = report.Verdicts.Count(v => v.Status == "new");
            if (changed > 0)
            {
                _shared.Text = report.SharedHosts.Count == 0
                    ? "This key is not trusted by any other host. If you did not rebuild this machine, treat the change as suspicious until proven otherwise."
                    : $"The same key is already trusted by {report.SharedHosts.Count} other host(s): {string.Join(", ", report.SharedHosts.Take(8))}"
                      + (report.SharedHosts.Count > 8 ? "…" : "") +
                      " — a shared key that suddenly changes everywhere is a different story from one host that changed alone.";
            }
            else if (match > 0 && fresh > 0)
            {
                _shared.Text = $"{match} key type(s) match what is stored; {fresh} other type(s) the server offers are not recorded yet — ssh records the one it actually negotiates, so this is normal.";
            }
            else if (match > 0)
            {
                _shared.Text = "Every key type the server offered matches known_hosts. Nothing to do.";
            }
            else
            {
                _shared.Text = "No stored key for this host yet; the first connection will record it.";
            }

            _note.Text = report.Note ?? "";
        }
        catch (Exception ex)
        {
            _status.Text = "Host key check failed: " + ex.Message;
        }
    }

    private void Forget()
    {
        if (_report is null || _report.Stored.Count == 0) return;
        var answer = MessageBox.Show(this,
            $"Remove {_report.Stored.Count} known_hosts entr{(_report.Stored.Count == 1 ? "y" : "ies")} for " +
            $"{_report.Host}:{_report.Port}?\n\nA timestamped .bak is written beside the file first. " +
            "The next connection will record whatever key the host presents.",
            "SSH-it", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) return;

        if (HostKeys.Forget(_report.Host, _report.Port, out var removed, out var backup))
        {
            _status.Text = $"Removed {removed} entr{(removed == 1 ? "y" : "ies")} · backup {backup}";
            _ = LoadAsync();
        }
        else
        {
            _status.Text = "Nothing removed — the file may have changed. Recheck.";
        }
    }
}
