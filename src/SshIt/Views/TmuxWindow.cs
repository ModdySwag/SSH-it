using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SshIt.Models;
using SshIt.Services;
using SshIt.Terminal;

namespace SshIt.Views;

/// <summary>
/// A window that draws a tmux session through control mode: one terminal pane per tmux pane, laid
/// out from `%layout-change`, keyboard input as `send-keys -H`, resize as `refresh-client -C` and a
/// pause recovered with `capture-pane`. No ConPTY anywhere in this path.
///
/// The window owns the <see cref="TmuxLink"/>; the panes share it. That is why closing the window is
/// what detaches the client, and why a single pane cannot "disconnect" on its own.
/// </summary>
public sealed class TmuxWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Session? _sshSession;
    private readonly string? _localDistro;
    private readonly string? _sessionName;
    private readonly Grid _host = new();
    private readonly TextBlock _status;
    private readonly TextBlock _error;
    private readonly List<TerminalPane> _panes = new();
    private readonly DispatcherTimer _layoutTimer;
    private readonly DispatcherTimer _resizeTimer;

    private TmuxLink? _link;
    private string? _layoutSignature;
    private string _sessionLabel = "";
    private string _windowLabel = "";
    private int _activePaneId = -1;
    private int _sentColumns;
    private int _sentRows;
    private bool _attaching;

    /// <summary>The panes this window owns, for the agent bridge and for tests.</summary>
    internal IEnumerable<TerminalPane> Panes() => _panes;

    /// <param name="sshSession">null for the local WSL target; otherwise the SSH settings to reuse.</param>
    /// <param name="sessionName">tmux session name; blank attaches to the most recent session.</param>
    /// <param name="localDistro">WSL distribution for the local target; blank uses the default.</param>
    public TmuxWindow(Session? sshSession, string? sessionName, AppSettings settings, string? localDistro = null)
    {
        _settings = settings;
        _sshSession = sshSession;
        _localDistro = localDistro;
        _sessionName = sessionName;

        Title = "tmux — SSH-it";
        Width = 1000;
        Height = 640;
        MinWidth = 480;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = (Brush)Application.Current.FindResource("Brush.Window");
        Foreground = (Brush)Application.Current.FindResource("Brush.Text");
        FontFamily = (FontFamily)Application.Current.FindResource("Font.UI");

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.MenuBar"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6, 10, 6)
        };
        var headerRow = new DockPanel();
        var detach = new Button
        {
            Content = "Detach",
            Padding = new Thickness(12, 3, 12, 3),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        AutomationProperties.SetName(detach, "Detach tmux");
        detach.Click += (_, _) => Close();
        DockPanel.SetDock(detach, Dock.Right);
        headerRow.Children.Add(detach);

        var heading = new TextBlock
        {
            Text = sshSession is null
                ? "tmux control mode · " + (string.IsNullOrWhiteSpace(localDistro) ? "WSL" : localDistro)
                : "tmux control mode · " + sshSession.DisplayTarget,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        headerRow.Children.Add(heading);
        header.Child = headerRow;
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        _host.Background = (Brush)Application.Current.FindResource("Brush.Panel");
        Grid.SetRow(_host, 1);
        root.Children.Add(_host);

        _error = new TextBlock
        {
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted"),
            Visibility = Visibility.Collapsed
        };
        Grid.SetRow(_error, 1);
        root.Children.Add(_error);

        var statusBar = new Border
        {
            Background = (Brush)Application.Current.FindResource("Brush.MenuBar"),
            BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10, 4, 10, 4)
        };
        _status = new TextBlock
        {
            Text = "Attaching…",
            FontSize = 11,
            Foreground = (Brush)Application.Current.FindResource("Brush.TextMuted")
        };
        AutomationProperties.SetName(_status, "tmux status");
        statusBar.Child = _status;
        Grid.SetRow(statusBar, 2);
        root.Children.Add(statusBar);

        Content = root;

        // tmux emits a layout change for every resize; rebuilding panes on each one would dispose
        // buffers mid-keystroke. Coalesce them, and coalesce the client-size command the same way.
        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _layoutTimer.Tick += (_, _) => { _layoutTimer.Stop(); RebuildFromLink(); };
        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); ApplyClientSize(); };

        SizeChanged += (_, _) => { _resizeTimer.Stop(); _resizeTimer.Start(); };
        Loaded += async (_, _) => await AttachAsync();
    }

    private async Task AttachAsync()
    {
        if (_attaching) return;
        _attaching = true;
        try
        {
            // Ask the server what it is before attaching: a tmux older than 3.0 does not answer the
            // attach query at all (measured on 2.9), so without this the window would sit empty until
            // its timeout instead of saying why.
            var version = await TmuxVersion.ProbeAsync(_sshSession, _localDistro);
            if (version is { } found && !TmuxVersion.IsSupported(found))
            {
                ShowError(TmuxVersion.Refusal(found, _sshSession?.DisplayTarget ?? (_localDistro ?? "the local WSL tmux")));
                return;
            }

            _link = _sshSession is null
                ? TmuxLink.StartLocal(_localDistro, _sessionName, Dispatcher)
                : TmuxLink.StartSsh(_sshSession, CredentialVault.GetSessionSecret(_sshSession.Id),
                    _sessionName, Dispatcher);
            _link.Event += OnTmuxEvent;
            _link.Exited += OnTmuxExited;

            // The first command is also the readiness check: if tmux is missing or the session name
            // does not exist, its %error text comes back here instead of an empty window. The marker
            // matters: tmux sends an unprompted %begin/%end pair right after attach, and a plain FIFO
            // would hand its empty text to this query.
            var marker = "SSHIT-ATTACH-" + Guid.NewGuid().ToString("n")[..6];
            var answer = await _link.QueryAsync(
                "display-message -p '" + marker + "|#{session_name}|#{window_layout}'", 25000, marker);
            Log.Info("tmux attach: " + answer.Replace(marker, "<marker>"));

            var trimmed = answer.Trim();
            var first = trimmed.IndexOf('|');
            if (first < 0) throw new InvalidOperationException("tmux answered without its marker: " + trimmed);
            var rest = trimmed[(first + 1)..];
            var second = rest.IndexOf('|');
            _sessionLabel = second < 0 ? rest : rest[..second];
            UpdateTitle();

            if (second >= 0)
            {
                var layoutText = rest[(second + 1)..].Trim();
                if (layoutText.Length > 0) Build(TmuxLayout.Parse(layoutText));
            }

            UpdateStatus();
        }
        catch (Exception ex)
        {
            var detail = ex.Message;
            if (_link?.LastError is { Length: > 0 } tail && !detail.Contains(tail, StringComparison.Ordinal))
                detail += "\n" + tail;
            ShowError("Could not attach to tmux:\n\n" + detail);
        }
        finally { _attaching = false; }
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        _status.Text = message.Replace('\n', ' ');
    }

    private void OnTmuxEvent(TmuxEvent produced)
    {
        switch (produced.Kind)
        {
            case TmuxEventKind.LayoutChange:
                _layoutTimer.Stop();
                _layoutTimer.Start();
                break;

            case TmuxEventKind.WindowRenamed when produced.Text.Length > 0:
                // The window's name (usually the running program), not the session's: replacing the
                // session label here turned the title into "tmux - bash - SSH-it".
                _windowLabel = produced.Text;
                UpdateTitle();
                break;

            case TmuxEventKind.SessionChanged when produced.Text.Length > 0:
                _sessionLabel = produced.Text;
                UpdateTitle();
                UpdateStatus();
                break;
        }
    }

    private void UpdateTitle()
    {
        var session = string.IsNullOrWhiteSpace(_sessionLabel) ? _link?.Description ?? "tmux" : _sessionLabel;
        var window = string.IsNullOrWhiteSpace(_windowLabel) ? "" : " · " + _windowLabel;
        Title = "tmux — " + session + window + " — SSH-it";
    }

    private void OnTmuxExited(string reason)
    {
        _status.Text = "Detached: " + reason;
    }

    private void RebuildFromLink()
    {
        if (_link?.Layout is not { } layout) return;
        Build(layout);
    }

    /// <summary>
    /// Builds one terminal pane per leaf of the layout. A rebuild keeps the scrollback of panes that
    /// survived by pane id: tmux numbers panes globally and stably, so that identity is the key.
    /// </summary>
    private void Build(TmuxLayout layout)
    {
        var signature = Signature(layout);
        Log.Info($"tmux build signature={signature} (previous={_layoutSignature ?? "none"})");
        if (signature == _layoutSignature) return;
        _layoutSignature = signature;

        var map = TmuxGridMap.Map(layout);
        Log.Info($"tmux grid columns={map.ColumnWidths.Count} rows={map.RowHeights.Count} cells={map.Cells.Count}");

        // A rebuild must not cost the user their scrollback: keep the old buffers by pane id and
        // move their lines into the new pane before the capture repaints the visible screen.
        var history = _panes.Where(p => p.TmuxPaneId >= 0)
            .GroupBy(p => p.TmuxPaneId)
            .ToDictionary(g => g.Key, g => g.First().Buffer);

        // Detach before disposing so an event in flight cannot paint into a dead pane.
        foreach (var pane in _panes.ToList()) pane.Dispose();
        _panes.Clear();
        _host.Children.Clear();
        _host.ColumnDefinitions.Clear();
        _host.RowDefinitions.Clear();

        foreach (var width in map.ColumnWidths)
            _host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width, GridUnitType.Star) });
        foreach (var height in map.RowHeights)
            _host.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height, GridUnitType.Star) });

        foreach (var cell in map.Cells)
        {
            var session = new Session
            {
                Name = $"tmux %{cell.PaneId}",
                Kind = "Tmux",
                Host = _sshSession?.Host ?? "wsl",
                User = _sshSession?.User ?? ""
            };

            var pane = new TerminalPane(session, _settings, _link!, cell.PaneId,
                Math.Max(2, cell.Width), Math.Max(1, cell.Height));
            if (history.TryGetValue(cell.PaneId, out var previous)) pane.Buffer.AdoptHistoryFrom(previous);
            pane.GotKeyboardFocus += (sender, _) =>
            {
                if (sender is TerminalPane focused) { _activePaneId = focused.TmuxPaneId; UpdateStatus(); }
            };

            var frame = new Border
            {
                BorderBrush = (Brush)Application.Current.FindResource("Brush.Border"),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = pane,
                Tag = cell.PaneId
            };
            Grid.SetColumn(frame, cell.Column);
            Grid.SetColumnSpan(frame, cell.ColumnSpan);
            Grid.SetRow(frame, cell.Row);
            Grid.SetRowSpan(frame, cell.RowSpan);

            _host.Children.Add(frame);
            _panes.Add(pane);
        }

        // The old buffers are gone; ask each pane for its screen so a rebuild is not a blank window.
        foreach (var pane in _panes) pane.RequestTmuxResync();

        if (_activePaneId >= 0 && _panes.FirstOrDefault(p => p.TmuxPaneId == _activePaneId) is { } active)
            active.FocusTerminal();
        else if (_panes.Count > 0)
        {
            _activePaneId = _panes[0].TmuxPaneId;
            _panes[0].FocusTerminal();
        }
        else _activePaneId = -1;

        ApplyClientSize(force: true);
        UpdateStatus();
    }

    private static string Signature(TmuxLayout layout) => string.Join(",", layout.Leaves()
        .Select(l => $"{l.PaneId}:{l.X},{l.Y},{l.Width}x{l.Height}")
        .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>
    /// tmux sizes the client, so the window tells it how many CELLS fit and lets the server lay the
    /// panes out. Sending pixels is meaningless in control mode.
    /// </summary>
    private void ApplyClientSize(bool force = false)
    {
        if (_link is null || _panes.Count == 0) return;
        var cellWidth = _panes[0].View.CellWidth;
        var cellHeight = _panes[0].View.CellHeight;
        if (cellWidth <= 0 || cellHeight <= 0) return;

        var columns = Math.Max(20, (int)Math.Floor(_host.ActualWidth / cellWidth));
        var rows = Math.Max(5, (int)Math.Floor(_host.ActualHeight / cellHeight));
        if (!force && columns == _sentColumns && rows == _sentRows) return;
        _sentColumns = columns;
        _sentRows = rows;
        _link.Send(TmuxControlParser.ResizeClient(columns, rows));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_link is null) return;
        var active = _activePaneId >= 0 ? $" · %{_activePaneId} active" : "";
        var size = _sentColumns > 0 ? $" · {_sentColumns}x{_sentRows}" : "";
        _status.Text = $"{_link.Description} · {_panes.Count} pane{(_panes.Count == 1 ? "" : "s")}{active}{size}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _layoutTimer.Stop();
        _resizeTimer.Stop();
        foreach (var pane in _panes.ToList())
        {
            try { pane.Dispose(); } catch { }
        }
        _panes.Clear();

        if (_link is not null)
        {
            _link.Event -= OnTmuxEvent;
            _link.Exited -= OnTmuxExited;
            try { _link.Dispose(); } catch { }
            _link = null;
        }

        base.OnClosed(e);
    }
}