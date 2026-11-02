using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// Fleet posture: every saved SSH host in one read-only table, worst first, with the fix for each
/// finding. Nothing here connects with the vault or changes a host; it runs the same two probes as
/// Diagnose (handshake and host keys) once per saved session and scores what the servers advertise.
/// </summary>
public sealed class FleetPostureWindow : Window
{
    private readonly IReadOnlyList<Session> _sessions;
    private readonly ListView _hosts = new();
    private readonly ListView _findings = new();
    private readonly TextBox _filter = new();
    private readonly ComboBox _severity = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _detail = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _scan = new();
    private readonly Button _stop = new();
    private readonly Button _export = new();
    private readonly Button _copy = new();

    private readonly List<HostPosture> _reports = new();
    private CancellationTokenSource? _cts;
    private bool _scanning;

    public FleetPostureWindow(IReadOnlyList<Session> sessions)
    {
        _sessions = sessions;
        Title = "Fleet posture";
        Width = 1240;
        Height = 740;
        MinWidth = 880;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        _scan.Content = "Scan saved hosts";
        _scan.Height = 28;
        _scan.Padding = new Thickness(12, 0, 12, 0);
        _scan.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _scan.Click += (_, _) => _ = RunScan();

        _stop.Content = "Stop";
        _stop.Height = 28;
        _stop.Margin = new Thickness(8, 0, 0, 0);
        _stop.Padding = new Thickness(12, 0, 12, 0);
        _stop.IsEnabled = false;
        _stop.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _stop.Click += (_, _) => _cts?.Cancel();

        _export.Content = "Export…";
        _export.Height = 28;
        _export.Margin = new Thickness(8, 0, 0, 0);
        _export.Padding = new Thickness(12, 0, 12, 0);
        _export.IsEnabled = false;
        _export.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _export.ToolTip = "CSV for a spreadsheet, Markdown for a ticket or wiki.";
        _export.Click += (_, _) => Export();

        _copy.Content = "Copy report";
        _copy.Height = 28;
        _copy.Margin = new Thickness(8, 0, 0, 0);
        _copy.Padding = new Thickness(12, 0, 12, 0);
        _copy.IsEnabled = false;
        _copy.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _copy.Click += (_, _) => CopyReport();

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
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 6), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_copy, Dock.Right);
        DockPanel.SetDock(_export, Dock.Right);
        DockPanel.SetDock(_stop, Dock.Right);
        DockPanel.SetDock(_scan, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(_copy);
        header.Children.Add(_export);
        header.Children.Add(_stop);
        header.Children.Add(_scan);
        header.Children.Add(_status);

        // Second row: filters and the summary.
        _filter.Width = 220;
        _filter.Margin = new Thickness(0, 0, 8, 0);
        _filter.ToolTip = "Filter by session name, host or server version";
        _filter.TextChanged += (_, _) => RefreshHosts();

        _severity.Width = 170;
        _severity.Margin = new Thickness(0, 0, 12, 0);
        _severity.Items.Add("All hosts");
        _severity.Items.Add("With findings");
        _severity.Items.Add("High only");
        _severity.Items.Add("Medium and above");
        _severity.SelectedIndex = 0;
        _severity.SelectionChanged += (_, _) => RefreshHosts();

        _summary.FontSize = 11;
        _summary.VerticalAlignment = VerticalAlignment.Center;
        _summary.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        var filterRow = new DockPanel { Margin = new Thickness(14, 0, 14, 4) };
        DockPanel.SetDock(_filter, Dock.Left);
        DockPanel.SetDock(_severity, Dock.Left);
        filterRow.Children.Add(_filter);
        filterRow.Children.Add(_severity);
        filterRow.Children.Add(_summary);

        _progress.Height = 3;
        _progress.IsIndeterminate = true;
        _progress.Visibility = Visibility.Collapsed;
        _progress.Margin = new Thickness(14, 0, 14, 0);

        // Left: one row per host, worst first.
        var hostView = new GridView();
        hostView.Columns.Add(Column("Session", "Session", 140));
        hostView.Columns.Add(Column("Host", "Host", 160));
        hostView.Columns.Add(Column("Server", "Server", 170));
        hostView.Columns.Add(Column("Risk", "Risk", 90));
        _hosts.View = hostView;
        _hosts.BorderThickness = new Thickness(0);
        _hosts.SelectionChanged += (_, _) => ShowHost();

        var left = new DockPanel { Margin = new Thickness(14, 0, 7, 0) };
        var leftTitle = new TextBlock
        {
            Text = "Saved hosts",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(leftTitle, Dock.Top);
        left.Children.Add(leftTitle);
        left.Children.Add(_hosts);

        // Right: the findings for the selected host, then the evidence behind them.
        var findingView = new GridView();
        findingView.Columns.Add(Column("Severity", "Severity", 80));
        findingView.Columns.Add(Column("Area", "Area", 115));
        findingView.Columns.Add(Column("Finding", "Item", 300));
        _findings.View = findingView;
        _findings.BorderThickness = new Thickness(0);
        _findings.Height = 210;
        _findings.SelectionChanged += (_, _) => ShowDetail();

        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.FontSize = 12;
        _detail.Margin = new Thickness(0, 8, 0, 0);
        var detailScroll = new ScrollViewer
        {
            Content = _detail,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        var right = new DockPanel { Margin = new Thickness(7, 0, 14, 0) };
        var rightTitle = new TextBlock
        {
            Text = "Findings",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(rightTitle, Dock.Top);
        DockPanel.SetDock(_findings, Dock.Top);
        right.Children.Add(rightTitle);
        right.Children.Add(_findings);
        right.Children.Add(detailScroll);

        var splitter = new GridSplitter
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            Background = (Brush)Application.Current.FindResource("Brush.Border")
        };

        var body = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(splitter);
        body.Children.Add(right);

        var footer = new TextBlock
        {
            Margin = new Thickness(14, 0, 14, 12),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Text = "Points: high 10, medium 4, low 1 — a sort key, not a rating. "
                   + "SSH-it contacts each host once for the handshake and once for its host keys; nothing is changed."
        };

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(filterRow, Dock.Top);
        DockPanel.SetDock(_progress, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(filterRow);
        root.Children.Add(_progress);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        Closed += (_, _) => _cts?.Cancel();

        var scannable = FleetPosture.Scannable(_sessions);
        if (scannable.Count == 0)
        {
            _status.Text = "No saved SSH sessions to scan.";
            _scan.IsEnabled = false;
            _detail.Text = "Save an SSH session first; local shells and serial ports have no handshake to measure.";
        }
        else
        {
            _status.Text = $"{scannable.Count} saved SSH host(s).";
            Loaded += (_, _) => _ = RunScan();
        }
    }

    private static GridViewColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new System.Windows.Data.Binding(path)
    };

    // ---------------------------------------------------------------- scanning

    private async Task RunScan()
    {
        if (_scanning) return;
        _scanning = true;
        _scan.IsEnabled = false;
        _stop.IsEnabled = true;
        _export.IsEnabled = false;
        _copy.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _reports.Clear();
        _hosts.Items.Clear();
        _findings.Items.Clear();
        _detail.Text = "";
        _summary.Text = "";

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        // Progress<T> captures this thread's context, so both callbacks run on the dispatcher.
        var started = new Progress<string>(message => _status.Text = message);
        var finished = new Progress<HostPosture>(posture =>
        {
            _reports.Add(posture);
            RefreshHosts();
            _status.Text = $"Scanned {_reports.Count} of {FleetPosture.Scannable(_sessions).Count}…";
        });

        try
        {
            await FleetPosture.ScanAsync(_sessions, started, finished, _cts.Token);
        }
        catch (Exception ex)
        {
            _status.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _scanning = false;
            _scan.IsEnabled = true;
            _stop.IsEnabled = false;
            _progress.Visibility = Visibility.Collapsed;

            // Completion order is not risk order; re-sort once everything is in.
            var sorted = _reports.OrderByDescending(r => r.Points)
                                 .ThenBy(r => r.SessionName, StringComparer.OrdinalIgnoreCase)
                                 .ToList();
            _reports.Clear();
            _reports.AddRange(sorted);
            RefreshHosts();
            _export.IsEnabled = _reports.Count > 0;
            _copy.IsEnabled = _reports.Count > 0;
            _status.Text = _reports.Count == 0 ? "Nothing scanned." : "Scan complete.";
        }
    }

    private void RefreshHosts()
    {
        var selected = (_hosts.SelectedItem as HostRow)?.Posture.SessionName;
        var needle = _filter.Text.Trim();
        var mode = Math.Max(0, _severity.SelectedIndex);

        var visible = _reports.Where(r =>
        {
            if (needle.Length > 0 &&
                r.SessionName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                r.Host.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                r.Target.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                (r.Software ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return false;

            return mode switch
            {
                1 => r.Worst != "clean",
                2 => r.Worst == "high",
                3 => r.Worst is "high" or "medium",
                _ => true
            };
        }).ToList();

        _hosts.Items.Clear();
        foreach (var posture in visible) _hosts.Items.Add(new HostRow(posture));

        _summary.Text = FleetPosture.Summary(_reports)
                        + (visible.Count == _reports.Count ? "" : $" · showing {visible.Count}");

        if (selected is not null)
        {
            var row = _hosts.Items.Cast<HostRow>().FirstOrDefault(r => r.Posture.SessionName == selected);
            if (row is not null) _hosts.SelectedItem = row;
        }
        if (_hosts.SelectedItem is null && _hosts.Items.Count > 0) _hosts.SelectedIndex = 0;
        if (_hosts.Items.Count == 0)
        {
            _findings.Items.Clear();
            _detail.Text = _reports.Count == 0 ? "" : "No host matches the filter.";
        }
    }

    private void ShowHost()
    {
        _findings.Items.Clear();
        if (_hosts.SelectedItem is not HostRow row) { _detail.Text = ""; return; }
        foreach (var finding in row.Posture.Findings) _findings.Items.Add(new FindingRow(finding));
        if (_findings.Items.Count > 0) _findings.SelectedIndex = 0;
        else ShowDetail();
    }

    private void ShowDetail()
    {
        if (_hosts.SelectedItem is not HostRow row) { _detail.Text = ""; return; }
        var posture = row.Posture;
        var text = new StringBuilder();

        if (_findings.SelectedItem is FindingRow finding)
        {
            text.AppendLine(finding.Finding.Severity.ToUpperInvariant() + " · "
                            + finding.Finding.Area + " · " + finding.Finding.Item);
            text.AppendLine();
            text.AppendLine(finding.Finding.Detail);
            if (finding.Finding.Fix.Length > 0)
            {
                text.AppendLine();
                text.AppendLine("Fix");
                text.AppendLine(finding.Finding.Fix);
            }
            text.AppendLine();
            text.AppendLine(new string('─', 40));
            text.AppendLine();
        }

        text.AppendLine(posture.SessionName + "  —  " + posture.Target + ":" + posture.Port);
        text.AppendLine();
        text.AppendLine("Server      " + (posture.Software ?? "not reported")
                        + (posture.Protocol is null ? "" : "  (protocol " + posture.Protocol + ")"));
        text.AppendLine("Negotiated  " + (posture.Kex ?? "-") + "  ·  " + (posture.HostKeyAlgo ?? "-")
                        + "  ·  " + (posture.CipherStoc ?? "-"));
        text.AppendLine("MAC         " + (posture.MacStoc ?? "-"));
        text.AppendLine("Auth        " + (posture.Auth.Count == 0 ? "-" : string.Join(", ", posture.Auth)));
        text.AppendLine("Verdict     " + posture.Worst + ", " + posture.Points + " point(s), " + posture.DurationMs + " ms"
                        + (posture.Error is null ? "" : "  —  " + posture.Error));

        if (posture.SshdConfig.Count > 0)
        {
            var keys = new[]
            {
                "permitrootlogin", "passwordauthentication", "kbdinteractiveauthentication",
                "permitemptypasswords", "maxauthtries", "pubkeyauthentication"
            };
            var pairs = keys.Where(k => posture.SshdConfig.ContainsKey(k))
                            .Select(k => k + " " + posture.SshdConfig[k]);
            text.AppendLine("sshd -T     " + string.Join("  ·  ", pairs));
        }
        else if (posture.SshdNote is not null)
        {
            text.AppendLine("sshd -T     " + posture.SshdNote);
        }

        if (posture.HostKeyVerdicts.Count > 0)
            text.AppendLine("Host keys   " + string.Join("  ·  ", posture.HostKeyVerdicts.Select(v =>
                v.KeyType + " " + v.Status + (v.Status == "match" ? " (" + v.Current + ")" : ""))));

        if (posture.OfferedHostKeys.Count > 0)
            text.AppendLine("Key sizes   " + string.Join("  ·  ", posture.OfferedHostKeys
                .Where(k => k.Bits > 0)
                .Select(k => k.Type + " " + k.Bits + "-bit")));

        if (posture.Offered.Kex.Count > 0)
            text.AppendLine();
        if (posture.Offered.Kex.Count > 0)
            text.AppendLine("Offered KEX  " + string.Join(", ", posture.Offered.Kex));
        if (posture.Offered.HostKey.Count > 0)
            text.AppendLine("Offered keys " + string.Join(", ", posture.Offered.HostKey));
        if (posture.Offered.CipherCtos.Count > 0)
            text.AppendLine("Offered ciphers  " + string.Join(", ", posture.Offered.CipherCtos));
        if (posture.Offered.MacCtos.Count > 0)
            text.AppendLine("Offered MACs     " + string.Join(", ", posture.Offered.MacCtos));

        _detail.Text = text.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- export

    private void Export()
    {
        if (_reports.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export fleet posture",
            FileName = "ssh-it-posture-" + DateTime.Now.ToString("yyyyMMdd-HHmm"),
            Filter = "CSV for a spreadsheet (*.csv)|*.csv|Markdown for a ticket (*.md)|*.md",
            FilterIndex = 1,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var markdown = dialog.FilterIndex == 2;
            var body = markdown ? FleetPosture.ToMarkdown(_reports) : FleetPosture.ToCsv(_reports);
            File.WriteAllText(dialog.FileName, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _status.Text = "Exported " + _reports.Count + " host(s) to " + dialog.FileName;
        }
        catch (Exception ex)
        {
            _status.Text = "Export failed: " + ex.Message;
        }
    }

    private void CopyReport()
    {
        if (_reports.Count == 0) return;
        try
        {
            Clipboard.SetText(FleetPosture.ToMarkdown(_reports));
            _status.Text = "Markdown report copied.";
        }
        catch (Exception ex)
        {
            _status.Text = "Clipboard: " + ex.Message;
        }
    }

    // ---------------------------------------------------------------- row shapes

    /// <summary>The ListView publishes an item's ToString() as its UIA name, so keep it meaningful.</summary>
    private sealed class HostRow
    {
        public HostRow(HostPosture posture) => Posture = posture;
        public HostPosture Posture { get; }
        public string Session => Posture.SessionName;
        public string Host => Posture.Host + (Posture.Port == 22 ? "" : ":" + Posture.Port);
        public string Server => Posture.Software ?? (Posture.Reachable ? "?" : "unreachable");
        public string Worst => Posture.Worst;
        /// <summary>One column instead of three: the severity word and the sort key together.</summary>
        public string Risk => Posture.Worst == "clean" ? "clean" : Posture.Worst + " " + Posture.Points;
        public override string ToString() => Session + " " + Host + " " + Worst;
    }

    private sealed class FindingRow
    {
        public FindingRow(PostureFinding finding) => Finding = finding;
        public PostureFinding Finding { get; }
        public string Severity => Finding.Severity;
        public string Area => Finding.Area;
        public string Item => Finding.Item;
        public override string ToString() => Severity + " " + Area + " " + Item;
    }
}
