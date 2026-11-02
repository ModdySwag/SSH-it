using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// Session &gt; Network scan. A bounded TCP sweep of one subnet on one port, with the answers
/// turned into saved sessions on request. It is deliberately not a port scanner: one port,
/// a hard address cap, and nothing written anywhere until a host is selected and added.
/// </summary>
public sealed class NetworkScanWindow : Window
{
    private readonly DataStore _store;
    private readonly TextBox _target = new();
    private readonly TextBox _port = new();
    private readonly TextBox _timeout = new();
    private readonly CheckBox _keyscan = new();
    private readonly Button _scan = new();
    private readonly Button _stop = new();
    private readonly Button _add = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _summary = new();
    private readonly ProgressBar _progress = new();
    private readonly ListView _hits = new();
    private readonly List<ScanHit> _results = new();

    private CancellationTokenSource? _cts;
    private bool _running;

    public NetworkScanWindow(DataStore store)
    {
        _store = store;
        Title = "Network scan";
        Width = 1180;
        Height = 680;
        MinWidth = 860;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        _scan.Content = "Scan";
        _scan.Height = 28;
        _scan.Padding = new Thickness(14, 0, 14, 0);
        _scan.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _scan.Click += (_, _) => _ = RunAsync();

        _stop.Content = "Stop";
        _stop.Height = 28;
        _stop.Margin = new Thickness(8, 0, 0, 0);
        _stop.Padding = new Thickness(12, 0, 12, 0);
        _stop.IsEnabled = false;
        _stop.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _stop.Click += (_, _) => _cts?.Cancel();

        _add.Content = "Add selected";
        _add.Height = 28;
        _add.Margin = new Thickness(8, 0, 0, 0);
        _add.Padding = new Thickness(12, 0, 12, 0);
        _add.IsEnabled = false;
        _add.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _add.ToolTip = "Create a session for every selected address. A host and port you already have is skipped.";
        _add.Click += (_, _) => AddSelected();

        var close = new Button
        {
            Content = "Close",
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(14, 0, 14, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar"),
            IsCancel = true
        };

        _target.Width = 230;
        _target.Margin = new Thickness(6, 0, 14, 0);
        _target.VerticalContentAlignment = VerticalAlignment.Center;
        _target.ToolTip = "192.168.1.0/24, 192.168.1.10-60, or a single address (IPv6 as one address)";
        AutomationProperties.SetAutomationId(_target, "ScanTarget");
        AutomationProperties.SetName(_target, "Addresses");

        _port.Width = 52;
        _port.Text = NetworkScan.DefaultPort.ToString();
        _port.Margin = new Thickness(6, 0, 14, 0);
        _port.VerticalContentAlignment = VerticalAlignment.Center;
        AutomationProperties.SetAutomationId(_port, "ScanPort");

        _timeout.Width = 58;
        _timeout.Text = NetworkScan.DefaultTimeoutMs.ToString();
        _timeout.Margin = new Thickness(6, 0, 14, 0);
        _timeout.VerticalContentAlignment = VerticalAlignment.Center;
        _timeout.ToolTip = "How long one address may take to answer, in milliseconds";
        AutomationProperties.SetAutomationId(_timeout, "ScanTimeout");

        _keyscan.Content = "Read host keys";
        _keyscan.IsChecked = true;
        _keyscan.Margin = new Thickness(0, 0, 12, 0);
        _keyscan.VerticalAlignment = VerticalAlignment.Center;
        _keyscan.ToolTip = "One ssh-keyscan per SSH host that answered. Shows the fingerprint you would verify.";

        _status.FontSize = 11;
        _status.Margin = new Thickness(12, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 6), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_add, Dock.Right);
        DockPanel.SetDock(_stop, Dock.Right);
        DockPanel.SetDock(_scan, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(_add);
        header.Children.Add(_stop);
        header.Children.Add(_scan);
        header.Children.Add(_status);

        var form = new DockPanel { Margin = new Thickness(14, 0, 14, 6), LastChildFill = true };
        var targetLabel = Label("Addresses");
        var portLabel = Label("Port");
        var timeoutLabel = Label("Timeout ms");
        var summaryHost = new DockPanel { LastChildFill = true };
        summaryHost.Children.Add(_summary);

        DockPanel.SetDock(targetLabel, Dock.Left);
        DockPanel.SetDock(_target, Dock.Left);
        DockPanel.SetDock(portLabel, Dock.Left);
        DockPanel.SetDock(_port, Dock.Left);
        DockPanel.SetDock(timeoutLabel, Dock.Left);
        DockPanel.SetDock(_timeout, Dock.Left);
        DockPanel.SetDock(_keyscan, Dock.Left);
        DockPanel.SetDock(summaryHost, Dock.Right);
        form.Children.Add(targetLabel);
        form.Children.Add(_target);
        form.Children.Add(portLabel);
        form.Children.Add(_port);
        form.Children.Add(timeoutLabel);
        form.Children.Add(_timeout);
        form.Children.Add(_keyscan);
        form.Children.Add(summaryHost);

        _summary.FontSize = 11;
        _summary.HorizontalAlignment = HorizontalAlignment.Right;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        _progress.Height = 3;
        _progress.IsIndeterminate = true;
        _progress.Visibility = Visibility.Collapsed;
        _progress.Margin = new Thickness(14, 0, 14, 4);

        _hits.Margin = new Thickness(14, 0, 14, 6);
        _hits.BorderThickness = new Thickness(0);
        _hits.SelectionMode = SelectionMode.Extended;
        AutomationProperties.SetAutomationId(_hits, "ScanResults");
        AutomationProperties.SetName(_hits, "Scan results");
        _hits.SelectionChanged += (_, _) => _add.IsEnabled = !_running && _hits.SelectedItems.Count > 0;
        var view = new GridView();
        view.Columns.Add(Column("Address", "Address", 150));
        view.Columns.Add(Column("Port", "Port", 55));
        view.Columns.Add(Column("Service", "Service", 70));
        view.Columns.Add(Column("Banner", "BannerText", 440));
        view.Columns.Add(Column("Fingerprint", "FingerprintText", 230));
        view.Columns.Add(Column("ms", "Milliseconds", 55));
        _hits.View = view;

        var footer = new TextBlock
        {
            Text = "TCP connect only — no ping and no port sweep: one port per run, up to 4,096 addresses. "
                 + "A host is a session only when you add it; this window writes nothing by itself.",
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 0, 14, 10),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(form, Dock.Top);
        DockPanel.SetDock(_progress, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(form);
        root.Children.Add(_progress);
        root.Children.Add(footer);
        root.Children.Add(_hits);
        Content = root;

        Loaded += (_, _) =>
        {
            _target.Text = NetworkScan.SuggestLocalBlock();
            _target.SelectAll();
            _target.Focus();
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        FontSize = 11,
        Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
    };

    private static GridViewColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new System.Windows.Data.Binding(path)
    };

    private void Note(string text, bool bad = false)
    {
        _status.Text = text;
        _status.Foreground = (Brush)Application.Current.FindResource(bad ? "Brush.Danger" : "Brush.TextMuted");
    }

    private async Task RunAsync()
    {
        if (_running) return;
        if (!NetworkScan.TryParseTargets(_target.Text, out var addresses, out var error))
        {
            Note(error, bad: true);
            return;
        }
        if (!int.TryParse(_port.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            Note("Port must be a whole number from 1 to 65535.", bad: true);
            return;
        }
        if (!int.TryParse(_timeout.Text.Trim(), out var timeoutMs) || timeoutMs is < 100 or > 10_000)
        {
            Note("Timeout must be 100 to 10000 milliseconds.", bad: true);
            return;
        }

        _running = true;
        _scan.IsEnabled = false;
        _stop.IsEnabled = true;
        _add.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _results.Clear();
        _hits.Items.Clear();
        _hits.SelectedItems.Clear();
        var total = addresses.Count;
        _summary.Text = $"0 of {total} tried";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Note("Scanning…");

        // Progress<T> captures this thread's context, so both callbacks run on the dispatcher.
        var done = new Progress<int>(count =>
            _summary.Text = $"{count} of {total} tried · {_results.Count} answered");
        var found = new Progress<ScanHit>(hit =>
        {
            _results.Add(hit);
            _hits.Items.Add(hit);
            _summary.Text = $"{_results.Count} answered so far";
        });

        try
        {
            await NetworkScan.SweepAsync(addresses, port, timeoutMs, NetworkScan.DefaultConcurrency,
                done, _cts.Token, found);

            if (_keyscan.IsChecked == true && _results.Any(hit => hit.IsSsh))
            {
                var sshCount = _results.Count(hit => hit.IsSsh);
                Note($"Reading host keys from {sshCount} SSH host{(sshCount == 1 ? "" : "s")}…");
                var keyed = new Progress<int>(count => Note($"Read {count} of {sshCount} host keys…"));
                await NetworkScan.FillFingerprintsAsync(_results, 3, 8, keyed, _cts.Token);
                _hits.Items.Refresh();
            }

            Note(_cts.IsCancellationRequested
                ? $"Stopped: {_results.Count} answered of {total} tried."
                : $"Scan complete: {_results.Count} answered of {total} tried.");
        }
        catch (OperationCanceledException)
        {
            Note($"Stopped: {_results.Count} answered of {total} tried.");
        }
        catch (Exception ex)
        {
            Note("Scan failed: " + ex.Message, bad: true);
        }
        finally
        {
            _running = false;
            _scan.IsEnabled = true;
            _stop.IsEnabled = false;
            _add.IsEnabled = _hits.SelectedItems.Count > 0;
            _progress.Visibility = Visibility.Collapsed;
        }
    }

    private void AddSelected()
    {
        var selected = _hits.SelectedItems.Cast<ScanHit>().ToList();
        if (selected.Count == 0)
        {
            Note("Select one or more hosts first.");
            return;
        }

        var created = NetworkScan.SessionsFor(selected, _store.Sessions, out var duplicates);
        foreach (var session in created) _store.Sessions.Add(session);
        if (created.Count > 0) _store.SaveSessions();

        Note(created.Count == 0
            ? $"Nothing added: {(duplicates == 1 ? "that host is" : "those hosts are")} already in the session list."
            : $"Added {created.Count} session{(created.Count == 1 ? "" : "s")}" +
              (duplicates > 0 ? $"; {duplicates} already saved." : "."));
    }
}
