using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// "Why did that connection do what it did?" — the effective configuration ssh resolved and a
/// live handshake walk, both from the user's own ssh binary. Read-only; it never changes a session.
/// </summary>
public sealed class DiagnoseWindow : Window
{
    private readonly Session? _session;
    /// <summary>What the pane already showed (its error line and scrollback tail), if this window
    /// was opened from a failed connection rather than from the menu.</summary>
    private readonly string? _context;
    private readonly TextBox _filter = new();
    private readonly ListView _config = new();
    private readonly ListView _steps = new();
    private readonly TextBlock _hints = new();
    private readonly TextBlock _footer = new();
    private readonly TextBlock _status = new();
    private readonly Button _run = new();
    private List<KeyValuePair<string, string>> _rows = new();
    private SshDiagnostics.Report? _report;

    public DiagnoseWindow(Session? session, string? context = null)
    {
        _session = session;
        _context = context;
        Title = "Diagnose connection" + (session is null ? "" : " — " + session.DisplayTarget);
        Width = 980;
        Height = 660;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        _run.Content = "Run checks";
        _run.Height = 28;
        _run.Padding = new Thickness(12, 0, 12, 0);
        _run.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _run.Click += (_, _) => _ = RunChecks();

        var copy = new Button
        {
            Content = "Copy report",
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar")
        };
        copy.Click += (_, _) => CopyReport();

        var hostKeys = new Button
        {
            Content = "Host keys…",
            Height = 28,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar"),
            ToolTip = "Known_hosts entries for this host, what it offers now, and what changed."
        };
        hostKeys.Click += (_, _) =>
        {
            if (_session is not null) new HostKeyWindow(_session) { Owner = this }.ShowDialog();
        };

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
        DockPanel.SetDock(copy, Dock.Left);
        DockPanel.SetDock(_run, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(copy);
        header.Children.Add(hostKeys);
        header.Children.Add(_run);
        header.Children.Add(_status);

        // Left: the effective configuration, filterable.
        _filter.Margin = new Thickness(0, 4, 0, 6);
        _filter.ToolTip = "Filter settings (for example: identity, proxy, cipher)";
        _filter.TextChanged += (_, _) => Fill(_filter.Text);

        var configView = new GridView();
        configView.Columns.Add(new GridViewColumn { Header = "Setting", Width = 168, DisplayMemberBinding = new System.Windows.Data.Binding("Key") });
        configView.Columns.Add(new GridViewColumn { Header = "Value", Width = 320, DisplayMemberBinding = new System.Windows.Data.Binding("Value") });
        _config.View = configView;
        _config.BorderThickness = new Thickness(0);

        var left = new DockPanel { Margin = new Thickness(14, 0, 7, 0) };
        var leftTitle = new TextBlock
        {
            Text = "Effective configuration (ssh -G)",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(leftTitle, Dock.Top);
        DockPanel.SetDock(_filter, Dock.Top);
        left.Children.Add(leftTitle);
        left.Children.Add(_filter);
        left.Children.Add(_config);

        // Right: the handshake walk and the meaning.
        var stepView = new GridView();
        stepView.Columns.Add(new GridViewColumn { Header = "Stage", Width = 118, DisplayMemberBinding = new System.Windows.Data.Binding("Stage") });
        stepView.Columns.Add(new GridViewColumn { Header = "What ssh reported", Width = 300, DisplayMemberBinding = new System.Windows.Data.Binding("Detail") });
        _steps.View = stepView;
        _steps.BorderThickness = new Thickness(0);
        _steps.Height = 250;

        _hints.TextWrapping = TextWrapping.Wrap;
        _hints.Margin = new Thickness(0, 6, 0, 0);
        _hints.FontSize = 12;
        var hintScroll = new ScrollViewer
        {
            Content = _hints,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 0)
        };

        var right = new DockPanel { Margin = new Thickness(7, 0, 14, 0) };
        var stepsTitle = new TextBlock
        {
            Text = "Handshake (ssh -vvv)",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var hintsTitle = new TextBlock
        {
            Text = "What this means",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0)
        };
        DockPanel.SetDock(stepsTitle, Dock.Top);
        DockPanel.SetDock(_steps, Dock.Top);
        DockPanel.SetDock(hintsTitle, Dock.Top);
        right.Children.Add(stepsTitle);
        right.Children.Add(_steps);
        right.Children.Add(hintsTitle);
        right.Children.Add(hintScroll);

        var splitter = new GridSplitter
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            Background = (Brush)Application.Current.FindResource("Brush.Border")
        };

        var body = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(right, 2);
        body.Children.Add(left);
        body.Children.Add(splitter);
        body.Children.Add(right);

        var footer = _footer;
        footer.Margin = new Thickness(14, 0, 14, 12);
        footer.Text = "";
        footer.FontSize = 10;
        footer.TextTrimming = TextTrimming.CharacterEllipsis;
        footer.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        if (session is null)
        {
            _status.Text = "Select a session first.";
            _run.IsEnabled = false;
        }
        else
        {
            _status.Text = "Ready.";
            Loaded += (_, _) => _ = RunChecks();
        }
    }

    private async Task RunChecks()
    {
        if (_session is null) return;
        _run.IsEnabled = false;
        _status.Text = "Probing " + _session.DisplayTarget + "…";
        _hints.Text = "";
        _steps.Items.Clear();
        _config.Items.Clear();
        _rows = new List<KeyValuePair<string, string>>();
        _report = null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var report = await SshDiagnostics.RunAsync(_session, cts.Token);
            _report = report;
            _rows = report.Effective;
            Fill(_filter.Text);
            foreach (var step in report.Steps) _steps.Items.Add(step);

            if (report.ProbeError is not null)
                _status.Text = "Probe failed: " + report.ProbeError;
            else if (report.FailureStage is not null)
                _status.Text = $"Stopped at {report.FailureStage} · exit {report.ExitCode} · {report.DurationMs} ms";
            else
                _status.Text = (report.Authenticated ? "Handshake completed and authenticated" : "Handshake completed")
                               + $" · exit {report.ExitCode} · {report.DurationMs} ms";

            // The probe explains the attempt it just made; the pane explains the one that failed.
            // Keep both, in that order, and never repeat a hint.
            var hints = new List<string>(report.Hints);
            if (_context is not null) hints.AddRange(SshDiagnostics.HintsForPane(_context, hints));

            var hintText = new StringBuilder();
            if (_context is not null)
            {
                hintText.AppendLine("The pane reported:");
                hintText.AppendLine(Clamp(_context, 700));
                hintText.AppendLine();
            }
            hintText.Append(hints.Count == 0
                ? "No known failure pattern in this attempt. The handshake list above is the full trace; the config list shows what ssh resolved."
                : string.Join("\n\n", hints.Select((h, i) => $"{i + 1}. {h}")));
            _hints.Text = hintText.ToString().TrimEnd();
            _hints.Foreground = (Brush)Application.Current.FindResource(
                report.FailureStage is null ? "Brush.TextMuted" : "Brush.Text");
            _steps.Background = Brushes.Transparent;
            _steps.Items.Refresh();

            var line = report.CommandLine;
            if (line.Length > 220) line = line[..220] + "…";
            _footer.Text = line;
            _footer.ToolTip = report.CommandLine;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Probe timed out after 45 s.";
            _hints.Text = "No answer within 45 seconds. That usually means a firewall dropping packets rather than refusing them.";
        }
        catch (Exception ex)
        {
            _status.Text = "Diagnose failed: " + ex.Message;
        }
        finally
        {
            _run.IsEnabled = true;
        }
    }

    private void Fill(string filter)
    {
        _config.Items.Clear();
        var needle = filter?.Trim() ?? "";
        foreach (var row in _rows)
        {
            if (needle.Length > 0 &&
                row.Key.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0 &&
                row.Value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
            _config.Items.Add(row);
        }
    }

    private static string Clamp(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private void CopyReport()
    {
        if (_report is null) return;
        var text = new StringBuilder();
        text.AppendLine("SSH-it diagnose report");
        text.AppendLine("host:      " + _report.Host);
        text.AppendLine("command:   " + _report.CommandLine);
        text.AppendLine("exit:      " + _report.ExitCode + "  (" + _report.DurationMs + " ms)");
        text.AppendLine("stage:     " + (_report.FailureStage ?? "completed"));
        text.AppendLine();
        text.AppendLine("-- effective configuration (ssh -G) --");
        foreach (var row in _report.Effective) text.AppendLine($"{row.Key} {row.Value}");
        text.AppendLine();
        text.AppendLine("-- handshake (ssh -vvv) --");
        foreach (var step in _report.Steps) text.AppendLine($"[{step.State}] {step.Stage}: {step.Detail}");
        if (_report.Hints.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("-- what this means --");
            foreach (var hint in _report.Hints) text.AppendLine("* " + hint);
        }
        try { Clipboard.SetText(text.ToString()); _status.Text = "Report copied."; }
        catch (Exception ex) { _status.Text = "Clipboard: " + ex.Message; }
    }
}
