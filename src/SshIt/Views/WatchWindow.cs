using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using SshIt.Models;
using SshIt.Services;

namespace SshIt.Views;

/// <summary>
/// watch(1) in a window: one command, rerun on an interval, with only the changed lines
/// highlighted and a receipt written when it stops. Read-only against the host — it runs the
/// command the user typed and nothing else.
/// </summary>
public sealed class WatchWindow : Window
{
    private readonly Session? _session;
    private readonly TextBox _command = new();
    private readonly TextBox _interval = new();
    private readonly RichTextBox _output = new();
    private readonly ListView _runs = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _loop = new();
    private readonly Button _start = new();
    private readonly Button _stop = new();
    private readonly Button _save = new();
    private CommandWatch? _watch;

    public WatchWindow(Session? session, string? lastCommand)
    {
        _session = session;
        Title = "Watch" + (session is null ? "" : " — " + session.DisplayTarget);
        Width = 980;
        Height = 700;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");
        FontSize = 12;

        _command.Text = lastCommand ?? "";
        _command.Height = 26;
        _command.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _command.ToolTip = "The command to rerun. Run inside a subshell, so `exit` cannot end the watch.";

        _interval.Text = "10";
        _interval.Width = 54;
        _interval.Height = 26;
        _interval.Margin = new Thickness(8, 0, 0, 0);
        _interval.TextAlignment = TextAlignment.Center;

        _start.Content = "Start";
        _start.Height = 26;
        _start.Padding = new Thickness(14, 0, 14, 0);
        _start.Margin = new Thickness(8, 0, 0, 0);
        _start.Style = (Style)Application.Current.FindResource("Btn.Accent");
        _start.Click += (_, _) => StartWatch();

        _stop.Content = "Stop";
        _stop.Height = 26;
        _stop.Padding = new Thickness(14, 0, 14, 0);
        _stop.Margin = new Thickness(8, 0, 0, 0);
        _stop.IsEnabled = false;
        _stop.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _stop.Click += (_, _) => StopWatch();

        _save.Content = "Save receipt…";
        _save.Height = 26;
        _save.Padding = new Thickness(12, 0, 12, 0);
        _save.Margin = new Thickness(8, 0, 0, 0);
        _save.IsEnabled = false;
        _save.Style = (Style)Application.Current.FindResource("Btn.Toolbar");
        _save.Click += (_, _) => SaveReceipt();

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
        _status.Margin = new Thickness(12, 0, 0, 0);
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        _status.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        // DockPanel lays its children out in CHILD ORDER, so the right-docked controls are added
        // right to left and the command box is added last to take the fill. Inserting a label at
        // index 0 (as an earlier version did) puts it at the far right and strips the fill from the
        // command box.
        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 6), LastChildFill = true };
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(_save, Dock.Right);
        DockPanel.SetDock(_stop, Dock.Right);
        DockPanel.SetDock(_start, Dock.Right);

        var seconds = new TextBlock
        {
            Text = "s",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        DockPanel.SetDock(seconds, Dock.Right);

        var intervalLabel = new TextBlock
        {
            Text = "every",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        DockPanel.SetDock(intervalLabel, Dock.Right);
        DockPanel.SetDock(_interval, Dock.Right);
        DockPanel.SetDock(_command, Dock.Left);

        header.Children.Add(close);
        header.Children.Add(_save);
        header.Children.Add(_stop);
        header.Children.Add(_start);
        header.Children.Add(seconds);
        header.Children.Add(_interval);
        header.Children.Add(intervalLabel);
        header.Children.Add(_command);

        // Named so a UIA probe (and a screen reader) can tell the two text fields apart.
        System.Windows.Automation.AutomationProperties.SetAutomationId(_command, "WatchCommand");
        System.Windows.Automation.AutomationProperties.SetName(_command, "Command to watch");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_interval, "WatchInterval");
        System.Windows.Automation.AutomationProperties.SetName(_interval, "Interval in seconds");

        // The remote loop, shown so nobody has to guess what is running on the host.
        _loop.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _loop.FontSize = 10;
        _loop.Margin = new Thickness(14, 0, 14, 6);
        _loop.TextTrimming = TextTrimming.CharacterEllipsis;
        _loop.Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted");

        // Latest run, with changed lines highlighted.
        _output.IsReadOnly = true;
        _output.FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono");
        _output.FontSize = 12;
        _output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _output.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        _output.Background = (Brush)Application.Current.FindResource("Brush.TerminalBackground");
        _output.Foreground = (Brush)Application.Current.FindResource("Brush.TerminalForeground");
        _output.BorderThickness = new Thickness(0);
        _output.Document.PagePadding = new Thickness(8);

        var runView = new GridView();
        runView.Columns.Add(Column("#", "Index", 40));
        runView.Columns.Add(Column("At", "At", 80));
        runView.Columns.Add(Column("Exit", "Exit", 50));
        runView.Columns.Add(Column("Changes", "Changes", 80));
        _runs.View = runView;
        _runs.BorderThickness = new Thickness(0);
        _runs.Width = 260;

        var right = new DockPanel { Margin = new Thickness(0, 0, 14, 0), Width = 268 };
        var runsTitle = new TextBlock
        {
            Text = "Runs",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var outputTitle = new TextBlock
        {
            Text = "Latest output (changed lines highlighted)",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(runsTitle, Dock.Top);
        DockPanel.SetDock(_runs, Dock.Top);
        _runs.Height = 240;
        right.Children.Add(runsTitle);
        right.Children.Add(_runs);

        var body = new Grid { Margin = new Thickness(14, 0, 0, 8) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new DockPanel();
        DockPanel.SetDock(outputTitle, Dock.Top);
        left.Children.Add(outputTitle);
        left.Children.Add(_output);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        body.Children.Add(left);
        body.Children.Add(right);

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_loop, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(_loop);
        root.Children.Add(_status);
        root.Children.Add(body);
        Content = root;

        Closed += (_, _) =>
        {
            StopWatch();
            _watch?.Dispose();
        };

        if (session is null || session.IsLocal)
        {
            _status.Text = session is null
                ? "Open a session first."
                : "Watch runs a command through ssh; a local shell has no host to watch.";
            _start.IsEnabled = false;
        }
        else
        {
            _status.Text = lastCommand is null
                ? "Type a command, then Start."
                : "Pre-filled with the last command of this tab.";
            _loop.Text = "ssh … " + Preview(new CommandWatch(session, _command.Text.Trim(), 10).RemoteLoop());
        }
    }

    private static GridViewColumn Column(string header, string path, double width) => new()
    {
        Header = header,
        Width = width,
        DisplayMemberBinding = new System.Windows.Data.Binding(path)
    };

    private static string Preview(string text) =>
        text.Length <= 150 ? text : text[..150] + "…";

    // ---------------------------------------------------------------- running

    private void StartWatch()
    {
        if (_session is null || _session.IsLocal) return;
        var command = _command.Text.Trim();
        if (command.Length == 0)
        {
            _status.Text = "Type a command to watch.";
            return;
        }
        if (!int.TryParse(_interval.Text.Trim(), out var seconds) || seconds < 1)
        {
            _status.Text = "The interval must be a whole number of seconds (1 or more).";
            return;
        }

        _watch?.Dispose();
        _runs.Items.Clear();
        _output.Document.Blocks.Clear();

        var watch = new CommandWatch(_session, command, seconds);
        watch.Ran += run => Dispatcher.BeginInvoke(() => ShowRun(run));
        watch.Note += note => Dispatcher.BeginInvoke(() => _status.Text = note);
        _watch = watch;

        _loop.Text = "ssh … " + Preview(watch.RemoteLoop());
        _start.IsEnabled = false;
        _stop.IsEnabled = true;
        _save.IsEnabled = false;
        watch.Start();
    }

    private void StopWatch()
    {
        if (_watch is null) return;
        _watch.Stop();
        _start.IsEnabled = _session is { IsLocal: false };
        _stop.IsEnabled = false;
        _save.IsEnabled = _watch.Runs.Count > 0;
    }

    private void ShowRun(WatchRun run)
    {
        _runs.Items.Insert(0, new RunRow(run));
        _status.Text = $"Run {run.Index} · exit {run.Exit} · +{run.Added.Count} −{run.Removed.Count}"
                       + (run.Changed ? " · changed" : " · unchanged");
        Render(run);
        _save.IsEnabled = true;
    }

    /// <summary>Unchanged lines in the terminal colour, added in success, removed in warning.</summary>
    private void Render(WatchRun run)
    {
        var added = new HashSet<string>(run.Added, StringComparer.Ordinal);
        var document = new FlowDocument
        {
            PagePadding = new Thickness(8),
            FontFamily = (FontFamily)Application.Current.FindResource("Font.Mono"),
            FontSize = 12,
            Background = Brushes.Transparent
        };

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var normal = (Brush)Application.Current.FindResource("Brush.TerminalForeground");
        var good = (Brush)Application.Current.FindResource("Brush.Success");
        var bad = (Brush)Application.Current.FindResource("Brush.Warn");

        foreach (var line in run.Output.Split('\n'))
        {
            var text = line.TrimEnd('\r');
            paragraph.Inlines.Add(new Run(text)
            {
                Foreground = added.Contains(text) ? good : normal,
                FontWeight = added.Contains(text) ? FontWeights.SemiBold : FontWeights.Normal
            });
            paragraph.Inlines.Add(new LineBreak());
        }

        if (run.Removed.Count > 0)
        {
            paragraph.Inlines.Add(new LineBreak());
            foreach (var line in run.Removed.Distinct())
                paragraph.Inlines.Add(new Run("− " + line) { Foreground = bad });
        }

        document.Blocks.Add(paragraph);
        _output.Document = document;
        _output.ScrollToHome();
    }

    private void SaveReceipt()
    {
        if (_watch is null || _watch.Runs.Count == 0) return;
        var dialog = new SaveFileDialog
        {
            Title = "Save watch receipt",
            FileName = "watch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"),
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FilterIndex = 1,
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, _watch.Receipt(), new UTF8Encoding(false));
            _status.Text = "Receipt saved to " + dialog.FileName;
        }
        catch (Exception ex)
        {
            _status.Text = "Could not save the receipt: " + ex.Message;
        }
    }

    private sealed class RunRow
    {
        public RunRow(WatchRun run)
        {
            Index = run.Index.ToString();
            At = run.At.ToString("HH:mm:ss");
            Exit = run.Exit.ToString();
            Changes = run.Changed ? $"+{run.Added.Count} −{run.Removed.Count}" : "";
        }

        public string Index { get; }
        public string At { get; }
        public string Exit { get; }
        public string Changes { get; }
        public override string ToString() => Index + " " + At + " exit " + Exit + " " + Changes;
    }
}
