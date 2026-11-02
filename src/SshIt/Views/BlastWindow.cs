using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// R3-19: what would this command touch? The classifier explains the risk and the host expands the
/// globs, so the answer to "what does * mean here" comes from the shell that would run it.
/// </summary>
public sealed class BlastWindow : Window
{
    private readonly Session? _session;
    private readonly TextBox _command = new();
    private readonly TextBlock _verdict = new();
    private readonly ListView _findings = new();
    private readonly ListView _matches = new();
    private readonly TextBlock _undo = new();
    private readonly TextBlock _status = new();
    private readonly Button _check = new();
    private readonly Button _snapshot = new();

    public BlastWindow(Session? session, string? lastCommand)
    {
        _session = session;
        Title = "Blast radius" + (session is null ? "" : " — " + session.DisplayTarget);
        Width = 1000;
        Height = 640;
        MinWidth = 720;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        _command.Text = lastCommand ?? "";
        _command.Height = 26;
        _command.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_command, "BlastCommand");
        System.Windows.Automation.AutomationProperties.SetName(_command, "Command to inspect");

        _check.Content = "Check on host";
        _check.Height = 26;
        _check.Padding = new Thickness(14, 0, 14, 0);
        _check.Margin = new Thickness(8, 0, 0, 0);
        _check.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _check.Click += (_, _) => _ = Check();

        var close = new Button
        {
            Content = "Close",
            Height = 26,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)Application.Current.FindResource("Btn.Toolbar"),
            IsCancel = true
        };

        _snapshot.Content = "Snapshot first";
        _snapshot.Height = 26;
        _snapshot.Padding = new Thickness(12, 0, 12, 0);
        _snapshot.Margin = new Thickness(8, 0, 0, 0);
        _snapshot.IsEnabled = false;
        _snapshot.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _snapshot.ToolTip = "Take a snapshot of the directory this command would touch, and show the restore command.";
        System.Windows.Automation.AutomationProperties.SetAutomationId(_snapshot, "BlastSnapshot");
        _snapshot.Click += (_, _) => _ = TakeSnapshot();

        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 6), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_check, Dock.Right);
        DockPanel.SetDock(_snapshot, Dock.Right);
        DockPanel.SetDock(_command, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(_check);
        header.Children.Add(_snapshot);
        header.Children.Add(_command);

        _verdict.FontSize = 14;
        _verdict.FontWeight = FontWeights.SemiBold;
        _verdict.Margin = new Thickness(14, 0, 14, 6);
        _verdict.Text = session is null ? "Select a session first." : "Type a command, then check what it would touch.";

        var findingView = new GridView();
        findingView.Columns.Add(new GridViewColumn { Header = "", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding("Severity") });
        findingView.Columns.Add(new GridViewColumn { Header = "What", Width = 90, DisplayMemberBinding = new System.Windows.Data.Binding("Kind") });
        findingView.Columns.Add(new GridViewColumn { Header = "Why it matters", Width = 520, CellTemplate = Trimmed("Detail") });
        _findings.View = findingView;
        _findings.BorderThickness = new Thickness(0);
        _findings.Height = 170;

        var matchView = new GridView();
        matchView.Columns.Add(new GridViewColumn { Header = "Pattern", Width = 220, CellTemplate = Trimmed("Pattern") });
        matchView.Columns.Add(new GridViewColumn { Header = "Matches", Width = 70, DisplayMemberBinding = new System.Windows.Data.Binding("Count") });
        matchView.Columns.Add(new GridViewColumn { Header = "On the host", Width = 560, CellTemplate = Trimmed("Examples") });
        _matches.View = matchView;
        _matches.BorderThickness = new Thickness(0);
        _matches.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _matches.FontSize = 11;

        _undo.TextWrapping = TextWrapping.Wrap;
        _undo.FontSize = 11;
        _undo.Margin = new Thickness(14, 6, 14, 6);
        _undo.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        _status.FontSize = 10;
        _status.Margin = new Thickness(14, 0, 14, 10);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        _status.Text = "A preview is a snapshot, not a promise: anything can change before the command runs. "
                       + "Directories are counted as one entry each, and a symlink is reported as what it points to.";

        var body = new DockPanel { Margin = new Thickness(14, 0, 14, 0) };
        var findingsTitle = new TextBlock { Text = "What it does", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        var matchesTitle = new TextBlock { Text = "What it would touch (expanded on the host)", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) };
        DockPanel.SetDock(findingsTitle, Dock.Top);
        DockPanel.SetDock(_findings, Dock.Top);
        DockPanel.SetDock(matchesTitle, Dock.Top);
        body.Children.Add(findingsTitle);
        body.Children.Add(_findings);
        body.Children.Add(matchesTitle);
        body.Children.Add(_matches);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_verdict, Dock.Top);
        DockPanel.SetDock(_undo, Dock.Bottom);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_verdict);
        root.Children.Add(_undo);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        if (session is null || session.IsLocal)
        {
            _check.IsEnabled = false;
            _verdict.Text = session is null ? "Select a session first." : "A local shell has no host to expand patterns on.";
        }
        Loaded += (_, _) => _command.Focus();
    }

    /// <summary>
    /// R3-18 from the window: snapshot the directory the first operand would touch and leave the
    /// restore command on screen. The snapshot is recorded in the undo store and the evidence
    /// chain; the restore is never run from here.
    /// </summary>
    private async Task TakeSnapshot()
    {
        if (_session is null || _session.IsLocal) return;
        var patterns = BlastRadius.Operands(BlastRadius.Assess(_command.Text.Trim()));
        if (patterns.Count == 0) { _verdict.Text = "Nothing to snapshot for this command."; return; }

        _snapshot.IsEnabled = false;
        _verdict.Text = "Taking a snapshot of " + UndoStore.BasePath(patterns[0]) + " …";
        _verdict.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var (point, error) = await UndoStore.SnapshotAsync(_session, patterns[0], cts.Token);
            if (point is null)
            {
                _verdict.Text = "No snapshot: " + error;
                _verdict.Foreground = (Brush)Application.Current.FindResource("Brush.Danger");
                return;
            }

            _undo.Text = "Snapshot " + point.Id + " (" + point.Kind + "): " + point.Snapshot
                         + "\nRestore: " + point.Restore;
            _verdict.Text = "Snapshot taken — the restore command is below, and the point is recorded.";
            _verdict.Foreground = (Brush)Application.Current.FindResource("Brush.Success");
        }
        finally
        {
            _snapshot.IsEnabled = true;
        }
    }

    private async Task Check()
    {
        if (_session is null || _session.IsLocal) return;
        var command = _command.Text.Trim();
        if (command.Length == 0) { _verdict.Text = "Type a command to inspect."; return; }

        var findings = BlastRadius.Assess(command);
        _findings.ItemsSource = findings.Select(f => new FindingRow(f)).ToList();
        _matches.ItemsSource = null;
        _snapshot.IsEnabled = findings.Count > 0;
        _check.IsEnabled = false;
        _verdict.Text = "Checking " + command + " …";
        _verdict.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var report = await BlastRadius.PreviewAsync(_session, command, cts.Token);
            _matches.ItemsSource = report.Matches.Select(m => new MatchRow(m)).ToList();

            var items = report.TotalItems;
            var verdict = report.Findings.Count == 0
                ? "clear — nothing in this command is on the destructive list, so the globs were not expanded."
                : $"{report.Verdict} — {items} item(s) match" + (report.TotalDirectories > 0 ? $", {report.TotalDirectories} of them directories" : "")
                  + (report.Matches.Any(m => m.Truncated) ? " (lists capped at 40 each; the count is exact)" : "");
            _verdict.Text = verdict;
            _verdict.Foreground = (Brush)Application.Current.FindResource(
                report.Verdict == "clear" ? "Brush.Success" : report.Verdict == "destructive" ? "Brush.Danger" : "Brush.Warn");

            _undo.Text = report.Undo ?? (report.FileSystem is null ? "" : "Filesystem: " + report.FileSystem);
            if (report.Error is not null) _status.Text = "ssh said: " + report.Error;
        }
        catch (Exception ex)
        {
            _verdict.Text = "Could not check: " + ex.Message;
        }
        finally
        {
            _check.IsEnabled = true;
        }
    }

    /// <summary>
    /// A cell that ellipsizes instead of hard-clipping, with the full value in a tooltip: a path cut
    /// at the column edge reads exactly like a complete path, which is the wrong answer in a window
    /// whose whole job is telling you what will be deleted.
    /// </summary>
    private static DataTemplate Trimmed(string path)
    {
        var factory = new FrameworkElementFactory(typeof(TextBlock));
        factory.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        factory.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path));
        factory.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(path));
        return new DataTemplate { VisualTree = factory };
    }

    private sealed class FindingRow
    {
        public FindingRow(BlastFinding finding) { Severity = finding.Severity; Kind = finding.Kind; Detail = finding.Detail; }
        public string Severity { get; }
        public string Kind { get; }
        public string Detail { get; }
        public override string ToString() => Severity + " " + Kind;
    }

    private sealed class MatchRow
    {
        public MatchRow(BlastMatch match)
        {
            Pattern = match.Pattern;
            Count = match.Count.ToString();
            Examples = match.Count == 0
                ? "(nothing)"
                : string.Join(", ", match.Directories.Concat(match.Files).Take(4))
                  + (match.Count > 4 ? ", …" : "");
        }

        public string Pattern { get; }
        public string Count { get; }
        public string Examples { get; }
        public override string ToString() => Pattern + " " + Count;
    }
}
